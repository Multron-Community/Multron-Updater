using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    public enum UpdateResult { UpToDate, Updated, Busy, Failed }

    public enum UpdateStage { Checking, Downloading, Closing, Installing, Starting, Completed, Failed, Building }

    public sealed record UpdateProgress(
        string ProfileId,
        string ProfileName,
        UpdateStage Stage,
        string Message,
        int FileIndex = 0,
        int FileCount = 0,
        long BytesDone = 0,
        long BytesTotal = 0,
        string CurrentFile = "");

    public enum FileState { Unknown, UpToDate, Changed, New, NotOnGitHub }

    public sealed record FileStatus(string RelativePath, FileState State, long Size);

    public sealed class UpdateService : IDisposable
    {
        private readonly GitHubClient _github = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, VerifiedState> _verified = new();

        private sealed record LocalStamp(string Path, long Size, DateTime WriteTimeUtc);

        private sealed record VerifiedState(string Key, List<LocalStamp> Files);

        private sealed record StagedFile(string RelativePath, string LocalPath, string StagingPath);

        private sealed record PartResult(bool Updated, string Message, int Files, IReadOnlyCollection<string>? ManagedPaths = null);

        private sealed record AfterActions(bool CloseProgram, bool RestartProgram, bool RefreshEdge);

        private static List<LocalStamp> TakeStamps(IEnumerable<string> paths) =>
            paths.Select(path =>
            {
                var fi = new FileInfo(path);
                return fi.Exists ? new LocalStamp(path, fi.Length, fi.LastWriteTimeUtc) : new LocalStamp(path, -1, default);
            }).ToList();

        private static bool StampsStillMatch(List<LocalStamp> stamps)
        {
            foreach (var s in stamps)
            {
                var fi = new FileInfo(s.Path);
                if (!fi.Exists || fi.Length != s.Size || fi.LastWriteTimeUtc != s.WriteTimeUtc) return false;
            }
            return true;
        }

        public event Action<UpdateProgress>? Progress;

        public bool IsBusy => _gate.CurrentCount == 0;

        private readonly ConcurrentDictionary<string, bool> _rebuildRequested = new();
        private readonly ConcurrentDictionary<string, string> _failedBuilds = new();
        private bool _manualRun;

        public void RequestRebuild(string profileId) => _rebuildRequested[profileId] = true;

        public async Task<UpdateResult> CheckAndUpdateAsync(UpdateProfile p, bool force, CancellationToken ct = default, bool manual = false)
        {
            if (!await _gate.WaitAsync(0, ct)) return UpdateResult.Busy;
            _manualRun = manual;
            var name = p.DisplayName;
            try
            {
                if (!p.IsConfigured(out var err)) throw new InvalidOperationException("Settings are incomplete: " + err);
                Report(p, UpdateStage.Checking, p.UsesLocalFolder ? "Checking the local repository folder..." : "Checking GitHub...");

                var parts = new List<PartResult>();
                var errors = new List<string>();

                if (p.UsesLocalFolder)
                {
                    try { parts.Add(await CheckLocalFolderAsync(p, force, ct)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); Logger.Error($"[{name}] {ex.Message}"); }
                }

                if (p.UsesRepository)
                {
                    try { parts.Add(await CheckRepositoryAsync(p, force, ct)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); Logger.Error($"[{name}] {ex.Message}"); }
                }
                if (p.UsesReleases)
                {
                    var managed = new HashSet<string>(parts.SelectMany(x => x.ManagedPaths ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
                    try { parts.Add(await CheckReleaseAsync(p, force, required: p.Source == TargetSource.Releases, managed, ct)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(ex.Message); Logger.Error($"[{name}] {ex.Message}"); }
                }

                var message = string.Join(" · ", parts.Select(x => x.Message).Where(x => x.Length > 0));
                if (errors.Count > 0)
                {
                    Report(p, UpdateStage.Failed, string.Join(" ", errors));
                    return UpdateResult.Failed;
                }
                if (parts.Any(x => x.Updated))
                {
                    p.LastUpdateTime = DateTime.Now;
                    Logger.Success($"[{name}] {message}.");
                    var files = parts.Sum(x => x.Files);
                    Report(p, UpdateStage.Completed, message, files, files);
                    return UpdateResult.Updated;
                }
                Report(p, UpdateStage.Completed, message);
                return UpdateResult.UpToDate;
            }
            catch (Exception ex)
            {
                var msg = ex is OperationCanceledException ? "Cancelled." : ex.Message;
                Logger.Error($"[{name}] {msg}");
                Report(p, UpdateStage.Failed, msg);
                return UpdateResult.Failed;
            }
            finally { _gate.Release(); }
        }

        private async Task<PartResult> CheckRepositoryAsync(UpdateProfile p, bool force, CancellationToken ct)
        {
            var name = p.DisplayName;
            var sha = await _github.GetLatestCommitShaAsync(p, ct);
            var shortSha = sha[..Math.Min(7, sha.Length)];
            var key = sha + "|" + p.SourceKey + "|" + p.SelectionKey;
            var verifiedKey = p.Id + ":repo";
            var buildKey = BuildKey(p, sha);

            if (!force && _verified.TryGetValue(verifiedKey, out var known) && known.Key == key)
            {
                if (StampsStillMatch(known.Files) && !NeedsBuild(p, buildKey, force))
                    return new PartResult(false, $"Repository up to date (commit {shortSha})", 0, known.Files.Select(f => f.Path).ToList());
                if (!StampsStillMatch(known.Files))
                    Logger.Info($"[{name}] Local files were changed or deleted since the last check; comparing them with GitHub again.");
            }

            var files = await _github.GetFilesAsync(p, sha, ct);
            if (files.Count == 0)
                throw new InvalidOperationException($"Path '{p.RepoPath}' was not found in branch '{p.Branch}'.");

            if (p.OnlySelectedFiles)
            {
                var wanted = p.GetIncludedFiles();
                if (wanted.Count == 0)
                    throw new InvalidOperationException("\"Update only the checked files\" is on, but no file is checked in an enabled group.");
                files = files.Where(f => wanted.ContainsKey(f.RelativePath)).ToList();
                if (files.Count == 0)
                    throw new InvalidOperationException($"None of the checked files exist under '{p.RepoPath}' on GitHub.");
            }

            var localRoot = p.BuildEnabled ? p.EffectiveBuildSourceFolder : Path.GetFullPath(p.LocalFolder);
            var rootWithSlash = localRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var changed = new List<(RemoteFile Remote, string LocalPath)>();
            var localPaths = new List<string>();
            foreach (var f in files)
            {
                var local = Path.GetFullPath(Path.Combine(localRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!local.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) continue;
                localPaths.Add(local);
                if (force || !File.Exists(local) || ComputeGitBlobSha(local) != f.BlobSha)
                    changed.Add((f, local));
            }

            p.LastCommitSha = sha;
            if (changed.Count == 0)
            {
                _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
                if (NeedsBuild(p, buildKey, force))
                {
                    var built = await BuildAndInstallAsync(p, new List<StagedFile>(), null, buildKey, shortSha,
                        new AfterActions(true, p.RestartAfterUpdate, p.RefreshEdgeAfterUpdate), ct);
                    return new PartResult(built > 0, built > 0 ? $"Built commit {shortSha} and installed {built} file(s)" : $"Built commit {shortSha}; the program is unchanged", built);
                }
                Logger.Info($"[{name}] Repository files are up to date (commit {shortSha}).");
                return new PartResult(false, $"Repository up to date (commit {shortSha})", 0, p.BuildEnabled ? null : localPaths);
            }

            Logger.Info($"[{name}] New version in the repository (commit {shortSha}): {changed.Count} file(s) changed.");

            var staging = Path.Combine(AppSettings.DataFolder, "downloads", p.Id, "repo-" + shortSha);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            var staged = new List<StagedFile>();
            long total = Math.Max(1, changed.Sum(c => c.Remote.Size));
            long doneBefore = 0;
            var lastReport = DateTime.MinValue;
            for (int i = 0; i < changed.Count; i++)
            {
                var (remote, local) = changed[i];
                int index = i + 1;
                Report(p, UpdateStage.Downloading, "Downloading update...", index, changed.Count, doneBefore, total, remote.RelativePath);

                var dest = Path.Combine(staging, remote.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await _github.DownloadAsync(p, sha, remote, dest, bytes =>
                {
                    if ((DateTime.Now - lastReport).TotalMilliseconds < 80) return;
                    lastReport = DateTime.Now;
                    Report(p, UpdateStage.Downloading, "Downloading update...", index, changed.Count,
                        doneBefore + Math.Min(bytes, remote.Size), total, remote.RelativePath);
                }, ct);
                doneBefore += remote.Size;

                if (ComputeGitBlobSha(dest) != remote.BlobSha)
                {
                    if (IsLfsPointer(dest))
                        throw new InvalidOperationException($"'{remote.RepoPath}' is stored with Git LFS; LFS files are not supported.");
                    throw new InvalidOperationException($"Downloaded file '{remote.RepoPath}' failed verification (corrupt download).");
                }

                var sha256 = ComputeSha256(dest);
                var sidecar = await _github.TryGetRawTextAsync(p, sha, remote.RepoPath + ".sha256", ct);
                var expected = sidecar == null ? null : Regex.Match(sidecar, "[0-9a-fA-F]{64}") is { Success: true } m ? m.Value.ToLowerInvariant() : null;
                if (expected != null && expected != sha256)
                    throw new InvalidOperationException($"'{remote.RepoPath}' does not match its .sha256 file in the repository (SHA-256 {sha256[..12]}…, expected {expected[..12]}…).");
                Logger.Info($"[{name}] Downloaded {remote.RelativePath} ({FormatSize(remote.Size)}), SHA-256 {sha256[..16]}…" +
                            (expected != null ? " verified with the .sha256 file." : "."));

                staged.Add(new StagedFile(remote.RelativePath, local, dest));
            }
            Report(p, UpdateStage.Downloading, "Download complete", changed.Count, changed.Count, total, total);

            AfterActions actions;
            if (p.OnlySelectedFiles)
            {
                var groupOf = p.GetIncludedFiles();
                var groups = changed.Select(c => groupOf.TryGetValue(c.Remote.RelativePath, out var g) ? g : null)
                                    .Where(g => g != null).Distinct().ToList();
                foreach (var g in groups)
                    Logger.Info($"[{name}] Group \"{g!.Name}\": {changed.Count(c => groupOf[c.Remote.RelativePath] == g)} changed file(s).");
                bool restart = groups.Any(g => g!.RestartProgram);
                actions = new AfterActions(restart, restart, groups.Any(g => g!.RefreshEdge));
            }
            else
            {
                actions = new AfterActions(true, p.RestartAfterUpdate, p.RefreshEdgeAfterUpdate);
            }

            if (p.BuildEnabled)
            {
                var built = await BuildAndInstallAsync(p, staged, staging, buildKey, shortSha, actions, ct);
                _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
                return new PartResult(built > 0, built > 0
                    ? $"Built commit {shortSha} ({changed.Count} source file(s) changed) and installed {built} file(s)"
                    : $"Built commit {shortSha}; the program is unchanged", built);
            }

            await InstallStagedAsync(p, staged, actions, staging, ct);
            _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
            return new PartResult(true, $"Updated {changed.Count} file(s) from the repository (commit {shortSha})", changed.Count, localPaths);
        }

        /// <summary>A file of the local repository copy: path relative to the source and on disk.</summary>
        private sealed record LocalSourceFile(string RelativePath, string SourcePath, long Size, DateTime WriteTimeUtc);

        /// <summary>
        /// Files under <see cref="UpdateProfile.RepoPath"/> in the local repository copy (a single
        /// file or a folder; empty = the whole folder). <c>.git</c> folders are skipped.
        /// </summary>
        private static List<LocalSourceFile> GetLocalSourceFiles(UpdateProfile p)
        {
            var repoRoot = Path.GetFullPath(p.LocalRepoFolder.Trim());
            if (!Directory.Exists(repoRoot))
                throw new InvalidOperationException($"Local repository folder '{repoRoot}' was not found (is the drive or share connected?).");
            var rel = p.RepoPath.Replace('\\', '/').Trim('/');
            var source = rel.Length == 0 ? repoRoot : Path.GetFullPath(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            var repoWithSlash = repoRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (source != repoRoot && !source.StartsWith(repoWithSlash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Path '{p.RepoPath}' points outside the local repository folder.");

            var target = Path.GetFullPath(p.LocalFolder).TrimEnd(Path.DirectorySeparatorChar);
            var sourceDir = (File.Exists(source) ? Path.GetDirectoryName(source)! : source).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(sourceDir, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The local repository path and the target folder are the same folder.");

            var result = new List<LocalSourceFile>();
            if (File.Exists(source))
            {
                var fi = new FileInfo(source);
                result.Add(new LocalSourceFile(fi.Name, fi.FullName, fi.Length, fi.LastWriteTimeUtc));
                return result;
            }
            if (!Directory.Exists(source))
                throw new InvalidOperationException($"Path '{p.RepoPath}' was not found in the local repository folder '{repoRoot}'.");

            var targetWithSlash = target + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (relative.Split('/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) continue;
                // The target folder inside the source tree is not a source.
                if (file.StartsWith(targetWithSlash, StringComparison.OrdinalIgnoreCase)) continue;
                var fi = new FileInfo(file);
                result.Add(new LocalSourceFile(relative, fi.FullName, fi.Length, fi.LastWriteTimeUtc));
            }
            return result;
        }

        /// <summary>
        /// Update from a repository copy on disk: no internet. Files are compared with SHA-256,
        /// copied into a staging folder (a snapshot, so later changes to the copy do not mix in),
        /// verified again, and installed with the same flow as GitHub updates (maintenance,
        /// close, backup, replace, rollback, signature reload).
        /// </summary>
        private async Task<PartResult> CheckLocalFolderAsync(UpdateProfile p, bool force, CancellationToken ct)
        {
            var name = p.DisplayName;
            var files = await Task.Run(() => GetLocalSourceFiles(p), ct);
            if (p.OnlySelectedFiles)
            {
                var wanted = p.GetIncludedFiles();
                if (wanted.Count == 0)
                    throw new InvalidOperationException("\"Update only the checked files\" is on, but no file is checked in an enabled group.");
                files = files.Where(f => wanted.ContainsKey(f.RelativePath)).ToList();
                if (files.Count == 0)
                    throw new InvalidOperationException($"None of the checked files exist under '{p.RepoPath}' in the local repository folder.");
            }
            if (files.Count == 0)
                throw new InvalidOperationException($"No files under '{p.RepoPath}' in the local repository folder.");

            // Same source files (names, sizes, times) as last time and the installed files
            // untouched: nothing to hash.
            var key = string.Join("|", files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                                             .Select(f => $"{f.RelativePath}:{f.Size}:{f.WriteTimeUtc.Ticks}"))
                      + "|" + p.SourceKey + "|" + p.SelectionKey;
            var verifiedKey = p.Id + ":local";
            var buildKey = BuildKey(p, key);
            var sourceVersion = "local " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (!force && _verified.TryGetValue(verifiedKey, out var known) && known.Key == key && StampsStillMatch(known.Files) &&
                !NeedsBuild(p, buildKey, force))
                return new PartResult(false, "Up to date with the local repository folder", 0, known.Files.Select(f => f.Path).ToList());

            var localRoot = p.BuildEnabled ? p.EffectiveBuildSourceFolder : Path.GetFullPath(p.LocalFolder);
            var rootWithSlash = localRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var changed = new List<(LocalSourceFile Source, string LocalPath, string Sha256)>();
            var localPaths = new List<string>();
            await Task.Run(() =>
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var local = Path.GetFullPath(Path.Combine(localRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!local.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) continue;
                    localPaths.Add(local);
                    var sourceSha = ComputeSha256(f.SourcePath);
                    if (force || !File.Exists(local) || new FileInfo(local).Length != f.Size || ComputeSha256(local) != sourceSha)
                        changed.Add((f, local, sourceSha));
                }
            }, ct);

            if (changed.Count == 0)
            {
                _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
                if (NeedsBuild(p, buildKey, force))
                {
                    var built = await BuildAndInstallAsync(p, new List<StagedFile>(), null, buildKey, sourceVersion,
                        new AfterActions(true, p.RestartAfterUpdate, p.RefreshEdgeAfterUpdate), ct);
                    return new PartResult(built > 0, built > 0 ? $"Built the local source and installed {built} file(s)" : "Built the local source; the program is unchanged", built);
                }
                Logger.Info($"[{name}] Up to date with the local repository folder ({files.Count} file(s)).");
                return new PartResult(false, "Up to date with the local repository folder", 0, localPaths);
            }

            Logger.Info($"[{name}] Local repository folder: {changed.Count} file(s) changed.");
            var staging = Path.Combine(AppSettings.DataFolder, "downloads", p.Id, "local-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            var staged = new List<StagedFile>();
            long total = Math.Max(1, changed.Sum(c => c.Source.Size));
            long done = 0;
            for (int i = 0; i < changed.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (src, local, sha) = changed[i];
                Report(p, UpdateStage.Downloading, "Copying from the local repository folder...", i + 1, changed.Count, done, total, src.RelativePath);
                var dest = Path.Combine(staging, src.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await RetryAsync(() => File.Copy(src.SourcePath, dest, true), ct);
                done += src.Size;

                // The copy must be the file that was compared (not changed or cut short meanwhile).
                var stagedSha = ComputeSha256(dest);
                if (stagedSha != sha)
                    throw new InvalidOperationException($"'{src.RelativePath}' changed while it was being copied from the local repository folder; try again.");
                if (IsLfsPointer(dest))
                    throw new InvalidOperationException($"'{src.RelativePath}' is a Git LFS pointer in the local repository folder (run 'git lfs pull' there first).");
                var sidecar = src.SourcePath + ".sha256";
                if (File.Exists(sidecar) && Regex.Match(File.ReadAllText(sidecar), "[0-9a-fA-F]{64}") is { Success: true } m &&
                    m.Value.ToLowerInvariant() != stagedSha)
                    throw new InvalidOperationException($"'{src.RelativePath}' does not match its .sha256 file in the local repository folder.");
                Logger.Info($"[{name}] Copied {src.RelativePath} ({FormatSize(src.Size)}), SHA-256 {stagedSha[..16]}…");
                staged.Add(new StagedFile(src.RelativePath, local, dest));
            }
            Report(p, UpdateStage.Downloading, "Copy complete", changed.Count, changed.Count, total, total);

            AfterActions actions;
            if (p.OnlySelectedFiles)
            {
                var groupOf = p.GetIncludedFiles();
                var groups = changed.Select(c => groupOf.TryGetValue(c.Source.RelativePath, out var g) ? g : null)
                                    .Where(g => g != null).Distinct().ToList();
                bool restart = groups.Any(g => g!.RestartProgram);
                actions = new AfterActions(restart, restart, groups.Any(g => g!.RefreshEdge));
            }
            else
            {
                actions = new AfterActions(true, p.RestartAfterUpdate, p.RefreshEdgeAfterUpdate);
            }

            if (p.BuildEnabled)
            {
                var built = await BuildAndInstallAsync(p, staged, staging, buildKey, sourceVersion, actions, ct);
                _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
                return new PartResult(built > 0, built > 0
                    ? $"Built the local source ({changed.Count} file(s) changed) and installed {built} file(s)"
                    : "Built the local source; the program is unchanged", built);
            }

            await InstallStagedAsync(p, staged, actions, staging, ct);
            _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
            return new PartResult(true, $"Updated {changed.Count} file(s) from the local repository folder", changed.Count, localPaths);
        }

        private async Task<PartResult> CheckReleaseAsync(UpdateProfile p, bool force, bool required, HashSet<string> managedByRepository, CancellationToken ct)
        {
            var name = p.DisplayName;
            var pattern = p.EffectiveAssetPattern();
            if (pattern.Length == 0)
            {
                if (required) throw new InvalidOperationException("Enter the release file name to download (e.g. MyApp.exe or MyApp-*.zip).");
                return new PartResult(false, "", 0);
            }

            var localRoot = Path.GetFullPath(p.LocalFolder);
            bool installedFilesPresent = p.InstalledReleaseDigest != null &&
                p.InstalledReleaseFiles.All(f => File.Exists(Path.Combine(localRoot, f.Replace('/', Path.DirectorySeparatorChar))));

            var quickTag = await _github.GetLatestReleaseTagAsync(p, ct);
            if (quickTag == "")
            {
                if (required) throw new InvalidOperationException($"{p.Owner}/{p.Repo} has no published release yet.");
                return new PartResult(false, "No releases yet", 0);
            }
            if (!force && quickTag != null && quickTag == p.InstalledReleaseTag && installedFilesPresent)
                return new PartResult(false, $"Release {quickTag} is installed", 0);
            if (!force && quickTag != null && p.InstalledReleaseDigest == null && ProgramVersionMatches(p, quickTag, out var version))
            {
                Logger.Info($"[{name}] {p.ExeName} is already version {version}, which is release {quickTag}; nothing to download.");
                p.InstalledReleaseTag = quickTag;
                p.InstalledReleaseDigest = "version:" + version;
                p.InstalledReleaseFiles = new List<string>();
                return new PartResult(false, $"Release {quickTag} is installed", 0);
            }

            var release = await _github.GetLatestReleaseAsync(p, ct);
            if (release == null)
            {
                if (required) throw new InvalidOperationException($"{p.Owner}/{p.Repo} has no published release yet.");
                return new PartResult(false, "No releases yet", 0);
            }

            var assets = release.Assets.Where(a => MatchesPattern(a.Name, pattern)).ToList();
            if (assets.Count == 0)
            {
                var available = release.Assets.Count == 0 ? "it has no files" : "files: " + string.Join(", ", release.Assets.Select(a => a.Name).Take(8));
                if (required) throw new InvalidOperationException($"Release {release.Tag} has no file matching '{pattern}' ({available}).");
                if (p.InstalledReleaseTag != release.Tag)
                    Logger.Info($"[{name}] Release {release.Tag} has no file matching '{pattern}' ({available}); only the repository files are used.");
                p.InstalledReleaseTag = release.Tag;
                p.InstalledReleaseDigest = "";
                p.InstalledReleaseFiles = new List<string>();
                return new PartResult(false, "", 0);
            }

            var digest = string.Join(";", assets.Select(a => $"{a.Name}={a.Sha256 ?? a.UpdatedAt}"));
            if (!force && digest == p.InstalledReleaseDigest && installedFilesPresent)
            {
                p.InstalledReleaseTag = release.Tag;
                return new PartResult(false, $"Release {release.Tag} is installed", 0);
            }

            Logger.Info(release.Tag == p.InstalledReleaseTag
                ? $"[{name}] Release {release.Tag} files are missing or changed on this computer; installing them again."
                : $"[{name}] New release {release.Tag}: {string.Join(", ", assets.Select(a => a.Name))}.");
            var staging = Path.Combine(AppSettings.DataFolder, "downloads", p.Id, "release-" + string.Concat(release.Tag.Split(Path.GetInvalidFileNameChars())));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            long total = Math.Max(1, assets.Sum(a => a.Size));
            long doneBefore = 0;
            var lastReport = DateTime.MinValue;
            var staged = new List<StagedFile>();
            var rootWithSlash = localRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            for (int i = 0; i < assets.Count; i++)
            {
                var asset = assets[i];
                int index = i + 1;
                var download = Path.Combine(staging, "assets", asset.Name);
                Report(p, UpdateStage.Downloading, "Downloading release...", index, assets.Count, doneBefore, total, asset.Name);
                await _github.DownloadAssetAsync(p, asset, download, bytes =>
                {
                    if ((DateTime.Now - lastReport).TotalMilliseconds < 80) return;
                    lastReport = DateTime.Now;
                    Report(p, UpdateStage.Downloading, "Downloading release...", index, assets.Count,
                        doneBefore + Math.Min(bytes, asset.Size), total, asset.Name);
                }, ct);
                doneBefore += asset.Size;

                var size = new FileInfo(download).Length;
                if (size != asset.Size)
                    throw new InvalidOperationException($"Release file '{asset.Name}' has the wrong size ({size} instead of {asset.Size} bytes).");
                var sha256 = ComputeSha256(download);
                if (asset.Sha256 != null && asset.Sha256 != sha256)
                    throw new InvalidOperationException($"Release file '{asset.Name}' failed SHA-256 verification.");
                Logger.Info($"[{name}] Downloaded release file {asset.Name} ({FormatSize(asset.Size)}), SHA-256 {sha256[..16]}…" +
                            (asset.Sha256 != null ? " verified." : " (GitHub gave no checksum for this file)."));

                if (p.ExtractZipAssets && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var extractDir = Path.Combine(staging, "extracted", Path.GetFileNameWithoutExtension(asset.Name));
                    ZipFile.ExtractToDirectory(download, extractDir, true);
                    var root = SingleTopFolder(extractDir) ?? extractDir;
                    int before = staged.Count;
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                        var local = Path.GetFullPath(Path.Combine(localRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!local.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) continue;
                        staged.Add(new StagedFile(rel, local, file));
                    }
                    Logger.Info($"[{name}] Extracted {asset.Name}: {staged.Count - before} file(s).");
                }
                else
                {
                    var exeName = p.ExeName.Trim().Replace('\\', '/');
                    string targetName;
                    if (ProgramRunner.HasWildcard(exeName))
                        targetName = ProgramRunner.MatchesExeName(p, asset.Name) && exeName.Contains('/')
                            ? exeName[..(exeName.LastIndexOf('/') + 1)] + asset.Name
                            : asset.Name;
                    else
                        targetName = assets.Count == 1 && exeName.Length > 0 &&
                                     string.Equals(Path.GetExtension(exeName), Path.GetExtension(asset.Name), StringComparison.OrdinalIgnoreCase)
                            ? exeName
                            : asset.Name;
                    var local = Path.GetFullPath(Path.Combine(localRoot, targetName.Replace('/', Path.DirectorySeparatorChar)));
                    staged.Add(new StagedFile(targetName, local, download));
                }
            }
            Report(p, UpdateStage.Downloading, "Download complete", assets.Count, assets.Count, total, total);

            var skipped = staged.Where(s => managedByRepository.Contains(s.LocalPath)).ToList();
            if (skipped.Count > 0)
            {
                Logger.Info($"[{name}] Release {release.Tag}: {skipped.Count} file(s) skipped because the repository files keep them up to date ({string.Join(", ", skipped.Select(s => s.RelativePath).Take(5))}).");
                staged = staged.Except(skipped).ToList();
            }

            var changedFiles = staged.Where(s => force || !File.Exists(s.LocalPath) || ComputeSha256(s.LocalPath) != ComputeSha256(s.StagingPath)).ToList();
            if (changedFiles.Count > 0)
                await InstallStagedAsync(p, changedFiles, new AfterActions(true, p.RestartAfterUpdate, p.RefreshEdgeAfterUpdate), staging, ct);
            else
                try { Directory.Delete(staging, true); } catch { }

            p.InstalledReleaseTag = release.Tag;
            p.InstalledReleaseDigest = digest;
            p.InstalledReleaseFiles = staged.Select(s => s.RelativePath).ToList();

            return changedFiles.Count > 0
                ? new PartResult(true, $"Installed release {release.Tag} ({changedFiles.Count} file(s))", changedFiles.Count)
                : new PartResult(false, $"Release {release.Tag} is installed", 0);
        }

        private static string BuildKey(UpdateProfile p, string sourceVersion)
        {
            if (!p.BuildEnabled) return "";
            var settings = string.Join("\n", p.BuildShell, p.BuildCommands, p.BuildWorkingDir, p.BuildOutputPath, p.BuildEnvironment, p.LocalFolder);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings)))[..16];
            return sourceVersion + "|" + hash;
        }

        private bool NeedsBuild(UpdateProfile p, string buildKey, bool force)
        {
            if (!p.BuildEnabled) return false;
            if (force || _rebuildRequested.ContainsKey(p.Id)) return true;
            if (p.BuildLastSuccessKey == buildKey) return false;
            if (!_manualRun && _failedBuilds.TryGetValue(p.Id, out var failed) && failed == buildKey)
            {
                Logger.Info($"[{p.DisplayName}] The last build of this version failed; it is built again on a manual check (or after a new commit).");
                return false;
            }
            return true;
        }

        private async Task<int> BuildAndInstallAsync(UpdateProfile p, List<StagedFile> sourceFiles, string? sourceStaging,
                                                     string buildKey, string version, AfterActions actions, CancellationToken ct)
        {
            var name = p.DisplayName;
            var source = p.EffectiveBuildSourceFolder;
            Directory.CreateDirectory(source);
            foreach (var f in sourceFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.LocalPath)!);
                await RetryAsync(() => File.Copy(f.StagingPath, f.LocalPath, true), ct);
            }
            if (sourceStaging != null) { try { Directory.Delete(sourceStaging, true); } catch { } }
            if (sourceFiles.Count > 0) Logger.Info($"[{name}] {sourceFiles.Count} source file(s) updated in {source}.");

            var output = Path.GetFullPath(Path.Combine(source, p.BuildOutputPath.Trim()));
            var sourceWithSlash = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!output.StartsWith(sourceWithSlash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The build output folder '{p.BuildOutputPath}' must be inside the source folder.");
            if (p.BuildCleanOutput && Directory.Exists(output))
            {
                try { Directory.Delete(output, true); }
                catch (Exception ex) { Logger.Warn($"[{name}] Could not clean the build output folder: {ex.Message}"); }
            }

            _rebuildRequested.TryRemove(p.Id, out _);
            Report(p, UpdateStage.Building, "Building...");
            Logger.Info($"[{name}] Building {version} ({p.BuildShell}) in {source}...");
            var lastReport = DateTime.MinValue;
            var result = await BuildRunner.RunAsync(p, source, version, line =>
            {
                if ((DateTime.Now - lastReport).TotalMilliseconds < 150) return;
                lastReport = DateTime.Now;
                Report(p, UpdateStage.Building, line.Length > 140 ? line[..140] + "…" : line);
            }, ct);

            if (!result.Success)
            {
                _failedBuilds[p.Id] = buildKey;
                foreach (var line in result.Tail.Where(l => l.Trim().Length > 0).TakeLast(15))
                    Logger.Warn($"[{name}] build: {line}");
                throw new InvalidOperationException(result.TimedOut
                    ? $"The build did not finish within {p.BuildTimeoutMinutes} minute(s) and was stopped. The installed program was not changed. Build log: {BuildRunner.LogFile(p)}"
                    : $"The build failed (exit code {result.ExitCode}). The installed program was not changed. Build log: {BuildRunner.LogFile(p)}");
            }
            _failedBuilds.TryRemove(p.Id, out _);
            Logger.Success($"[{name}] Build finished in {result.Duration:mm\\:ss}.");

            if (!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException($"The build succeeded but the output folder '{output}' is empty or missing (check \"Build output folder\").");

            var target = Path.GetFullPath(p.LocalFolder);
            var staging = Path.Combine(AppSettings.DataFolder, "downloads", p.Id, "build-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var staged = new List<StagedFile>();
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(output, file).Replace('\\', '/');
                    var local = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(local) && new FileInfo(local).Length == new FileInfo(file).Length && ComputeSha256(local) == ComputeSha256(file))
                        continue;
                    var dest = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                    staged.Add(new StagedFile(rel, local, dest));
                }
            }, ct);

            if (staged.Count == 0)
            {
                p.BuildLastSuccessKey = buildKey;
                Logger.Info($"[{name}] The build output is the same as the installed program; nothing to install.");
                try { Directory.Delete(staging, true); } catch { }
                return 0;
            }
            Logger.Info($"[{name}] Installing {staged.Count} built file(s) into {target}.");
            await InstallStagedAsync(p, staged, actions, staging, ct);
            p.BuildLastSuccessKey = buildKey;
            return staged.Count;
        }

        /// <summary>Files the running program keeps loaded: updating one needs a restart.</summary>
        private static bool IsProgramFile(string path) =>
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        private async Task InstallStagedAsync(UpdateProfile p, List<StagedFile> files, AfterActions actions, string staging, CancellationToken ct, bool allowReload = true)
        {
            var name = p.DisplayName;
            string? exePath = ProgramRunner.ExePath(p);
            bool wasRunning = false;
            var requested = actions;

            // Multron server, no .exe / .dll changed: replace the files while it runs and
            // reload its signatures instead of closing and restarting it.
            bool reloadOnly = allowReload && p.ServerReloadSignatures && exePath != null && actions.CloseProgram &&
                              !files.Any(f => IsProgramFile(f.LocalPath)) && await ServerMaintenance.IsRunningAsync(p, ct);
            if (reloadOnly)
            {
                Logger.Info($"[{name}] No program files changed ({files.Count} file(s)); the server keeps running and reloads its signatures.");
                actions = actions with { CloseProgram = false, RestartProgram = false };
            }

            // Multron server, seamless update: the new version starts next to the running
            // one and takes over when it has loaded; scans are not stopped meanwhile.
            if (!reloadOnly && allowReload && p.ServerSeamlessUpdate && exePath != null && actions.CloseProgram && actions.RestartProgram)
            {
                var status = await ServerMaintenance.GetServerStatusAsync(p, ct);
                if (status is { Seamless: true })
                {
                    await SeamlessUpdateAsync(p, files, exePath, status.Pid, staging, actions, ct);
                    return;
                }
                if (status != null)
                    Logger.Info($"[{name}] The running server is too old for a seamless update; it is restarted the usual way this time (the next updates are seamless).");
            }

            if (exePath != null && actions.CloseProgram)
            {
                ServerMaintenance.Handle? maintenance = null;
                if (p.ServerMaintenance)
                {
                    Report(p, UpdateStage.Closing, "Switching the server to maintenance...");
                    maintenance = await ServerMaintenance.EnterUpdatingAsync(p, ct);
                }
                Report(p, UpdateStage.Closing, $"Closing {Path.GetFileName(exePath)}...");
                try { wasRunning = await ProgramRunner.CloseAllAsync(p, exePath); }
                catch
                {
                    // Still running: do not leave it refusing scans.
                    await ServerMaintenance.RestoreAsync(p, maintenance);
                    throw;
                }
            }

            var backup = Path.Combine(AppSettings.DataFolder, "backups", p.Id, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var replaced = new List<(string Local, string? Backup)>();
            try
            {
                Report(p, UpdateStage.Installing, "Installing update...");
                foreach (var f in files)
                {
                    string? bak = null;
                    if (File.Exists(f.LocalPath))
                    {
                        bak = Path.Combine(backup, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                        await RetryAsync(() => File.Copy(f.LocalPath, bak, true), ct);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(f.LocalPath)!);
                    await RetryAsync(() => File.Copy(f.StagingPath, f.LocalPath, true), ct);
                    replaced.Add((f.LocalPath, bak));
                }
                Logger.Info($"[{name}] {replaced.Count} file(s) replaced in {p.LocalFolder}.");
            }
            catch (Exception ex) when (reloadOnly && ex is not OperationCanceledException)
            {
                // A file is held open by the running server: put the old files back and
                // update the usual way (close, replace, start).
                Logger.Warn($"[{name}] Replacing files while the server runs failed ({ex.Message}); closing it for the update instead.");
                foreach (var (local, bak) in replaced)
                {
                    try { if (bak != null) File.Copy(bak, local, true); else File.Delete(local); } catch { }
                }
                await InstallStagedAsync(p, files, requested, staging, ct, allowReload: false);
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"[{name}] Replacing files failed, rolling back to the previous version: " + ex.Message);
                foreach (var (local, bak) in replaced)
                {
                    try { if (bak != null) File.Copy(bak, local, true); else File.Delete(local); } catch { }
                }
                if (exePath != null && wasRunning) ProgramRunner.Start(p, exePath);
                try { Directory.Delete(staging, true); } catch { }
                throw;
            }
            try { Directory.Delete(staging, true); } catch { }

            if (ProgramRunner.HasWildcard(p.ExeName))
            {
                var installed = files.Select(f => f.LocalPath).Where(f => ProgramRunner.MatchesExeName(p, f)).ToList();
                if (installed.Count > 0)
                {
                    foreach (var old in ProgramRunner.MatchingExePaths(p)
                                 .Where(f => !installed.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            Directory.CreateDirectory(backup);
                            await RetryAsync(() => File.Copy(old, Path.Combine(backup, Path.GetFileName(old)), true), ct);
                            await RetryAsync(() => File.Delete(old), ct);
                            Logger.Info($"[{name}] Removed the previous version {Path.GetFileName(old)}.");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Logger.Warn($"[{name}] Could not remove the previous version {Path.GetFileName(old)}: {ex.Message}");
                        }
                    }
                    exePath = installed[0];
                }
            }

            if (!p.KeepBackup) { try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { } }
            else if (Directory.Exists(backup))
            {
                Logger.Info($"[{name}] Backup of replaced files: {backup}");
                CleanupOldBackups(Path.Combine(AppSettings.DataFolder, "backups", p.Id), keep: 3);
            }

            if (reloadOnly)
            {
                Report(p, UpdateStage.Starting, "Reloading the server's signatures...");
                await ServerMaintenance.ReloadSignaturesAsync(p, ct);
            }

            if (exePath != null && actions.RestartProgram)
            {
                Report(p, UpdateStage.Starting, $"Starting {Path.GetFileName(exePath)}...");
                ProgramRunner.Start(p, exePath);
            }

            if (actions.RefreshEdge)
            {
                Report(p, UpdateStage.Starting, "Refreshing the Edge tab...");
                if (EdgeRefresher.RefreshActiveTab(out var edgeMsg)) Logger.Info($"[{name}] {edgeMsg}");
                else Logger.Warn($"[{name}] Edge refresh skipped: {edgeMsg}");
            }
        }

        /// <summary>
        /// Seamless update of a running Multron server: files the server holds open are
        /// renamed to <c>*.old-&lt;time&gt;</c> (Windows allows renaming a running exe) and the
        /// new ones are put in their place; the new version is started with <c>--handoff</c>
        /// and loads next to the running server, which keeps scanning ("getting updated")
        /// until the new one takes its ports over. The update counts as done when the
        /// dashboard answers from a new process. If the new version does not take over,
        /// the previous files are put back and the running server is left as it was.
        /// </summary>
        private async Task SeamlessUpdateAsync(UpdateProfile p, List<StagedFile> files, string exePath, int oldPid,
                                               string staging, AfterActions actions, CancellationToken ct)
        {
            var name = p.DisplayName;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backup = Path.Combine(AppSettings.DataFolder, "backups", p.Id, stamp);
            var replaced = new List<(string Local, string? Backup, string? Renamed)>();

            void Rollback()
            {
                foreach (var (local, bak, renamed) in Enumerable.Reverse(replaced))
                {
                    try
                    {
                        if (renamed != null)
                        {
                            if (File.Exists(local)) File.Delete(local);
                            File.Move(renamed, local);
                        }
                        else if (bak != null) File.Copy(bak, local, true);
                        else File.Delete(local);
                    }
                    catch (Exception ex) { Logger.Warn($"[{name}] Could not put back {local}: {ex.Message}"); }
                }
            }

            // Renamed copies of earlier seamless updates (their process has exited by now).
            foreach (var f in files) DeleteOldCopies(f.LocalPath);

            Report(p, UpdateStage.Installing, "Installing next to the running server...");
            try
            {
                foreach (var f in files)
                {
                    string? bak = null, renamed = null;
                    if (File.Exists(f.LocalPath))
                    {
                        bak = Path.Combine(backup, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                        await RetryAsync(() => File.Copy(f.LocalPath, bak, true), ct);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(f.LocalPath)!);
                    try { File.Copy(f.StagingPath, f.LocalPath, true); }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && File.Exists(f.LocalPath))
                    {
                        // Held open by the running server (its exe / dll): move it aside.
                        renamed = f.LocalPath + ".old-" + stamp;
                        File.Move(f.LocalPath, renamed);
                        replaced.Add((f.LocalPath, bak, renamed));
                        File.Copy(f.StagingPath, f.LocalPath, true);
                        continue;
                    }
                    replaced.Add((f.LocalPath, bak, null));
                }
                Logger.Info($"[{name}] {replaced.Count} file(s) replaced in {p.LocalFolder} while the server keeps running ({replaced.Count(r => r.Renamed != null)} in use, renamed to *.old-{stamp}).");
            }
            catch (Exception ex)
            {
                Logger.Error($"[{name}] Replacing files failed, rolling back; the server keeps running: " + ex.Message);
                Rollback();
                throw;
            }
            try { Directory.Delete(staging, true); } catch { }

            Report(p, UpdateStage.Starting, "Starting the new version next to the running server (seamless update)...");
            var drain = Math.Clamp(p.EffectiveServerDrainSeconds, 0, 300);
            ProgramRunner.Start(p, exePath, $"--handoff --handoff-drain-secs {drain}");
            Logger.Info($"[{name}] Seamless update: the running server (pid {oldPid}) keeps scanning while the new version loads.");

            var limit = TimeSpan.FromMinutes(Math.Clamp(p.ServerSeamlessMinutes, 1, 180));
            var started = DateTime.UtcNow;
            DateTime? downSince = null;
            bool sawStandby = false;
            int? standbyPid = null;
            string? failure = null;
            while (true)
            {
                await Task.Delay(2000, ct);
                var st = await ServerMaintenance.GetServerStatusAsync(p, ct);
                var now = DateTime.UtcNow;
                if (st == null)
                {
                    // The switch itself takes a few seconds; much longer means nobody took over.
                    downSince ??= now;
                    if (now - downSince.Value > TimeSpan.FromSeconds(90))
                    {
                        Logger.Warn($"[{name}] No server answers 90 s after the switch; starting it the usual way.");
                        ProgramRunner.Start(p, exePath);
                        break;
                    }
                    continue;
                }
                downSince = null;
                if (st.Pid != oldPid)
                {
                    Logger.Info($"[{name}] Seamless update done: the new version (pid {st.Pid}) took over from pid {oldPid}.");
                    break;
                }
                if (st.StandbyPid is int sp)
                {
                    if (!sawStandby) Report(p, UpdateStage.Starting, "The new version is loading; the running server keeps scanning...");
                    sawStandby = true;
                    standbyPid = sp;
                }
                else if (st.Mode != "switching" && (sawStandby || now - started > TimeSpan.FromSeconds(120)))
                {
                    failure = sawStandby
                        ? "the new version stopped before it took over (see its console log)"
                        : "the new version did not start (see its console log)";
                    break;
                }
                if (now - started > limit)
                {
                    failure = $"the new version did not finish loading in {limit.TotalMinutes:0} minutes";
                    if (standbyPid is int pid) await ProgramRunner.CloseByIdAsync(p, pid);
                    break;
                }
            }

            if (failure != null)
            {
                Rollback();
                throw new InvalidOperationException($"Seamless update failed: {failure}. The previous files were put back and the running server was left as it was.");
            }

            // The old process has exited: its renamed files can go now (or on the next update).
            await Task.Delay(1000, ct);
            foreach (var (local, _, renamed) in replaced)
                if (renamed != null) DeleteOldCopies(local);

            if (!p.KeepBackup) { try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { } }
            else if (Directory.Exists(backup))
            {
                Logger.Info($"[{name}] Backup of replaced files: {backup}");
                CleanupOldBackups(Path.Combine(AppSettings.DataFolder, "backups", p.Id), keep: 3);
            }

            if (actions.RefreshEdge)
            {
                Report(p, UpdateStage.Starting, "Refreshing the Edge tab...");
                if (EdgeRefresher.RefreshActiveTab(out var edgeMsg)) Logger.Info($"[{name}] {edgeMsg}");
                else Logger.Warn($"[{name}] Edge refresh skipped: {edgeMsg}");
            }
        }

        /// <summary>Deletes <c>file.old-*</c> copies left by seamless updates (skips ones still in use).</summary>
        private static void DeleteOldCopies(string file)
        {
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (dir == null || !Directory.Exists(dir)) return;
                foreach (var old in Directory.GetFiles(dir, Path.GetFileName(file) + ".old-*"))
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
        }

        public Task<ReleaseData?> GetLatestReleaseAsync(UpdateProfile p, CancellationToken ct = default) =>
            _github.GetLatestReleaseAsync(p, ct);

        public async Task<List<FileStatus>> GetFileStatusAsync(UpdateProfile p, CancellationToken ct = default)
        {
            if (p.UsesLocalFolder)
            {
                if (!p.IsConfigured(out var lerr)) throw new InvalidOperationException(lerr);
                return await Task.Run(() =>
                {
                    var localRoot = Path.GetFullPath(p.LocalFolder);
                    var list = new List<FileStatus>();
                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in GetLocalSourceFiles(p))
                    {
                        present.Add(f.RelativePath);
                        var local = Path.Combine(localRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        var state = !File.Exists(local) ? FileState.New
                                  : new FileInfo(local).Length == f.Size && ComputeSha256(local) == ComputeSha256(f.SourcePath) ? FileState.UpToDate
                                  : FileState.Changed;
                        list.Add(new FileStatus(f.RelativePath, state, f.Size));
                    }
                    foreach (var path in p.AllTrackedPaths().Distinct(StringComparer.OrdinalIgnoreCase))
                        if (!present.Contains(path)) list.Add(new FileStatus(path, FileState.NotOnGitHub, 0));
                    return list;
                }, ct);
            }
            if (!p.UsesRepository) throw new InvalidOperationException("This target only uses releases; there is no repository file list.");
            if (!p.IsConfigured(out var err)) throw new InvalidOperationException(err);
            var sha = await _github.GetLatestCommitShaAsync(p, ct);
            var files = await _github.GetFilesAsync(p, sha, ct);
            if (files.Count == 0)
                throw new InvalidOperationException($"Path '{p.RepoPath}' was not found in branch '{p.Branch}'.");

            var localRoot = Path.GetFullPath(p.LocalFolder);
            var result = new List<FileStatus>();
            var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                remotePaths.Add(f.RelativePath);
                var local = Path.Combine(localRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var state = !File.Exists(local) ? FileState.New
                          : ComputeGitBlobSha(local) == f.BlobSha ? FileState.UpToDate
                          : FileState.Changed;
                result.Add(new FileStatus(f.RelativePath, state, f.Size));
            }
            foreach (var path in p.AllTrackedPaths().Distinct(StringComparer.OrdinalIgnoreCase))
                if (!remotePaths.Contains(path)) result.Add(new FileStatus(path, FileState.NotOnGitHub, 0));
            return result;
        }

        private static bool ProgramVersionMatches(UpdateProfile p, string tag, out string version)
        {
            version = "";
            var exe = ProgramRunner.ExePath(p);
            if (exe == null || !File.Exists(exe)) return false;
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            version = (info.ProductVersion ?? info.FileVersion ?? "").Trim();
            if (version.Length < 3) return false;
            var cleanTag = tag.TrimStart('v', 'V');
            return cleanTag == version || cleanTag.StartsWith(version + "-") || cleanTag.StartsWith(version + "+");
        }

        private static readonly Regex VersionInName = new(
            @"(?<![\dA-Za-z])v?\d+(?:\.\d+)+(?:[-+](?:\d+(?:\.\d+)*|(?:alpha|beta|rc|pre|preview|dev)\.?\d*))*",
            RegexOptions.IgnoreCase);

        public static string VersionToWildcard(string fileName, string? releaseTag = null)
        {
            var tag = releaseTag?.Trim();
            foreach (var candidate in new[] { tag, tag?.TrimStart('v', 'V') })
            {
                if (string.IsNullOrEmpty(candidate) || candidate.Length < 2) continue;
                var i = fileName.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) return fileName[..i] + "*" + fileName[(i + candidate.Length)..];
            }
            return VersionInName.Replace(fileName, "*", 1);
        }

        public static bool MatchesPattern(string name, string patterns)
        {
            foreach (var raw in patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var rx = "^" + Regex.Escape(raw).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                if (Regex.IsMatch(name, rx, RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        private static string? SingleTopFolder(string dir)
        {
            if (Directory.EnumerateFiles(dir).Any()) return null;
            var subs = Directory.GetDirectories(dir);
            return subs.Length == 1 ? subs[0] : null;
        }

        private static async Task RetryAsync(Action action, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { action(); return; }
                catch (IOException) when (attempt < 20) { await Task.Delay(500, ct); }
                catch (UnauthorizedAccessException) when (attempt < 20) { await Task.Delay(500, ct); }
            }
        }

        private static void CleanupOldBackups(string folder, int keep)
        {
            try
            {
                var root = new DirectoryInfo(folder);
                if (!root.Exists) return;
                foreach (var d in root.GetDirectories().OrderByDescending(d => d.Name).Skip(keep))
                    d.Delete(true);
            }
            catch { }
        }

        public static string ComputeSha256(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        public static string ComputeGitBlobSha(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            h.AppendData(Encoding.ASCII.GetBytes($"blob {fs.Length}\0"));
            var buf = new byte[81920];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) h.AppendData(buf, 0, n);
            return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        }

        private static bool IsLfsPointer(string file)
        {
            try
            {
                if (new FileInfo(file).Length > 1024) return false;
                return File.ReadAllText(file).StartsWith("version https://git-lfs", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes; int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.0} {units[u]}";
        }

        private void Report(UpdateProfile p, UpdateStage stage, string message, int fileIndex = 0, int fileCount = 0,
                            long bytesDone = 0, long bytesTotal = 0, string currentFile = "") =>
            Progress?.Invoke(new UpdateProgress(p.Id, p.DisplayName, stage, message, fileIndex, fileCount, bytesDone, bytesTotal, currentFile));

        public void Dispose() { _github.Dispose(); _gate.Dispose(); }
    }
}
