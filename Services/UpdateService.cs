using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        private readonly ConcurrentDictionary<string, string> _verified = new();

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
                var sha = await _github.GetLatestCommitShaAsync(p, ct);
                var shortSha = sha[..Math.Min(7, sha.Length)];
                var key = sha + "|" + p.SourceKey + "|" + p.SelectionKey;

                if (!force && _verified.TryGetValue(p.Id, out var known) && known == key)
                {
                    Report(p, UpdateStage.Completed, $"Up to date (commit {shortSha})");
                    return UpdateResult.UpToDate;
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
                foreach (var f in files)
                {
                    var local = Path.GetFullPath(Path.Combine(localRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!local.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) continue;
                    if (force || !File.Exists(local) || ComputeGitBlobSha(local) != f.BlobSha)
                        changed.Add((f, local));
                }

                p.LastCommitSha = sha;
                if (changed.Count == 0)
                {
                    _verified[p.Id] = key;
                    Logger.Info($"[{name}] Checked: files are up to date (commit {shortSha}).");
                    Report(p, UpdateStage.Completed, $"Up to date (commit {shortSha})");
                    return UpdateResult.UpToDate;
                }

                Logger.Info($"[{name}] New version found (commit {shortSha}): {changed.Count} file(s) changed.");
                await ApplyUpdateAsync(p, sha, changed, ct);

                _verified[p.Id] = key;
                p.LastUpdateTime = DateTime.Now;
                var done = $"Updated {changed.Count} file(s) to commit {shortSha}";
                Logger.Success($"[{name}] {done}.");
                Report(p, UpdateStage.Completed, done, changed.Count, changed.Count);
                return UpdateResult.Updated;
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

        public async Task<List<FileStatus>> GetFileStatusAsync(UpdateProfile p, CancellationToken ct = default)
        {
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

        private async Task ApplyUpdateAsync(UpdateProfile p, string sha, List<(RemoteFile Remote, string LocalPath)> changed, CancellationToken ct)
        {
            var name = p.DisplayName;

            bool closeProgram, restartProgram, refreshEdge;
            if (p.OnlySelectedFiles)
            {
                var groupOf = p.GetIncludedFiles();
                var groups = changed.Select(c => groupOf.TryGetValue(c.Remote.RelativePath, out var g) ? g : null)
                                    .Where(g => g != null).Distinct().ToList();
                foreach (var g in groups)
                    Logger.Info($"[{name}] Group \"{g!.Name}\": {changed.Count(c => groupOf[c.Remote.RelativePath] == g)} changed file(s).");
                restartProgram = groups.Any(g => g!.RestartProgram);
                refreshEdge = groups.Any(g => g!.RefreshEdge);
                closeProgram = restartProgram;
            }
            else
            {
                closeProgram = true;
                restartProgram = p.RestartAfterUpdate;
                refreshEdge = p.RefreshEdgeAfterUpdate;
            }
            var staging = Path.Combine(AppSettings.DataFolder, "downloads", p.Id, sha[..7]);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);

            long total = Math.Max(1, changed.Sum(c => c.Remote.Size));
            long doneBefore = 0;
            var lastReport = DateTime.MinValue;
            for (int i = 0; i < changed.Count; i++)
            {
                var remote = changed[i].Remote;
                int index = i + 1;
                Report(p, UpdateStage.Downloading, "Downloading update...", index, changed.Count, doneBefore, total, remote.RelativePath);

                var dest = StagingPath(staging, remote);
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
                Logger.Info($"[{name}] Downloaded {remote.RelativePath} ({FormatSize(remote.Size)}).");
            }
            Report(p, UpdateStage.Downloading, "Download complete", changed.Count, changed.Count, total, total);
            Logger.Info($"[{name}] {changed.Count} file(s) downloaded and verified.");

            string? exePath = ProgramRunner.ExePath(p);
            bool wasRunning = false;
            if (exePath != null && closeProgram)
            {
                Report(p, UpdateStage.Closing, $"Closing {Path.GetFileName(exePath)}...");
                wasRunning = await ProgramRunner.CloseAsync(p, exePath);
            }

            var backup = Path.Combine(AppSettings.DataFolder, "backups", p.Id, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var replaced = new List<(string Local, string? Backup)>();
            try
            {
                Report(p, UpdateStage.Installing, "Installing update...");
                foreach (var (remote, local) in changed)
                {
                    string? bak = null;
                    if (File.Exists(local))
                    {
                        bak = Path.Combine(backup, remote.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                        await RetryAsync(() => File.Copy(local, bak, true), ct);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    await RetryAsync(() => File.Copy(StagingPath(staging, remote), local, true), ct);
                    replaced.Add((local, bak));
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

            if (exePath != null && restartProgram)
            {
                Report(p, UpdateStage.Starting, $"Starting {Path.GetFileName(exePath)}...");
                ProgramRunner.Start(p, exePath);
            }

            if (refreshEdge)
            {
                Report(p, UpdateStage.Starting, "Refreshing the Edge tab...");
                if (EdgeRefresher.RefreshActiveTab(out var edgeMsg)) Logger.Info($"[{name}] {edgeMsg}");
                else Logger.Warn($"[{name}] Edge refresh skipped: {edgeMsg}");
            }
        }

        private static string StagingPath(string staging, RemoteFile f) =>
            Path.Combine(staging, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));

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
