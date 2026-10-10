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

    public enum UpdateStage { Checking, Downloading, Closing, Installing, Starting, Completed, Failed }

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

        public async Task<UpdateResult> CheckAndUpdateAsync(UpdateProfile p, bool force, CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct)) return UpdateResult.Busy;
            var name = p.DisplayName;
            try
            {
                if (!p.IsConfigured(out var err)) throw new InvalidOperationException("Settings are incomplete: " + err);
                Report(p, UpdateStage.Checking, "Checking GitHub...");

                var parts = new List<PartResult>();
                var errors = new List<string>();

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

            if (!force && _verified.TryGetValue(verifiedKey, out var known) && known.Key == key)
            {
                if (StampsStillMatch(known.Files)) return new PartResult(false, $"Repository up to date (commit {shortSha})", 0, known.Files.Select(f => f.Path).ToList());
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

            var localRoot = Path.GetFullPath(p.LocalFolder);
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
                Logger.Info($"[{name}] Repository files are up to date (commit {shortSha}).");
                return new PartResult(false, $"Repository up to date (commit {shortSha})", 0, localPaths);
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

            await InstallStagedAsync(p, staged, actions, staging, ct);
            _verified[verifiedKey] = new VerifiedState(key, TakeStamps(localPaths));
            return new PartResult(true, $"Updated {changed.Count} file(s) from the repository (commit {shortSha})", changed.Count, localPaths);
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
                    var targetName = assets.Count == 1 && !string.IsNullOrWhiteSpace(p.ExeName) &&
                                     string.Equals(Path.GetExtension(p.ExeName), Path.GetExtension(asset.Name), StringComparison.OrdinalIgnoreCase)
                        ? p.ExeName.Trim().Replace('\\', '/')
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

        private async Task InstallStagedAsync(UpdateProfile p, List<StagedFile> files, AfterActions actions, string staging, CancellationToken ct)
        {
            var name = p.DisplayName;
            string? exePath = ProgramRunner.ExePath(p);
            bool wasRunning = false;
            if (exePath != null && actions.CloseProgram)
            {
                Report(p, UpdateStage.Closing, $"Closing {Path.GetFileName(exePath)}...");
                wasRunning = await ProgramRunner.CloseAsync(p, exePath);
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
            catch (Exception ex)
            {
                Logger.Error($"[{name}] Replacing files failed, rolling back to the previous version: " + ex.Message);
                foreach (var (local, bak) in replaced)
                {
                    try { if (bak != null) File.Copy(bak, local, true); else File.Delete(local); } catch { }
                }
                if (exePath != null && wasRunning) ProgramRunner.Start(p, exePath);
                throw;
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
            }

            if (!p.KeepBackup) { try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { } }
            else if (Directory.Exists(backup))
            {
                Logger.Info($"[{name}] Backup of replaced files: {backup}");
                CleanupOldBackups(Path.Combine(AppSettings.DataFolder, "backups", p.Id), keep: 3);
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

        public Task<ReleaseData?> GetLatestReleaseAsync(UpdateProfile p, CancellationToken ct = default) =>
            _github.GetLatestReleaseAsync(p, ct);

        public async Task<List<FileStatus>> GetFileStatusAsync(UpdateProfile p, CancellationToken ct = default)
        {
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
