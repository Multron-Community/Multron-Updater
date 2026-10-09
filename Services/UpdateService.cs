using System;
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
        UpdateStage Stage,
        string Message,
        int FileIndex = 0,
        int FileCount = 0,
        long BytesDone = 0,
        long BytesTotal = 0,
        string CurrentFile = "");

    /// <summary>
    /// Check → download → close exe → replace files → restart.
    /// Local files are compared with GitHub's blob SHA, so only changed files are downloaded.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        private readonly GitHubClient _github = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _lastVerifiedKey;   // commit+source whose files were verified as identical in this session

        /// <summary>Raised from a background thread.</summary>
        public event Action<UpdateProgress>? Progress;

        public bool IsBusy => _gate.CurrentCount == 0;

        public async Task<UpdateResult> CheckAndUpdateAsync(AppSettings s, bool force, CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct)) return UpdateResult.Busy;

            try
            {
                if (!s.IsConfigured(out var err)) throw new InvalidOperationException("Settings are incomplete: " + err);

                Report(new UpdateProgress(UpdateStage.Checking, "Checking GitHub..."));
                var sha = await _github.GetLatestCommitShaAsync(s, ct);
                var shortSha = sha[..Math.Min(7, sha.Length)];
                var key = sha + "|" + s.SourceKey;

                if (!force && key == _lastVerifiedKey)
                {
                    Report(new UpdateProgress(UpdateStage.Completed, $"Up to date (commit {shortSha})"));
                    return UpdateResult.UpToDate;
                }

                var files = await _github.GetFilesAsync(s, sha, ct);
                if (files.Count == 0)
                    throw new InvalidOperationException($"Path '{s.RepoPath}' was not found in branch '{s.Branch}'.");

                var localRoot = Path.GetFullPath(s.LocalFolder);
                var changed = new List<(RemoteFile Remote, string LocalPath)>();
                foreach (var f in files)
                {
                    var local = Path.GetFullPath(Path.Combine(localRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!local.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the target folder
                    if (force || !File.Exists(local) || ComputeGitBlobSha(local) != f.BlobSha)
                        changed.Add((f, local));
                }

                s.LastCommitSha = sha;
                if (changed.Count == 0)
                {
                    _lastVerifiedKey = key;
                    Logger.Info($"Checked: files are up to date (commit {shortSha}).");
                    Report(new UpdateProgress(UpdateStage.Completed, $"Up to date (commit {shortSha})"));
                    return UpdateResult.UpToDate;
                }

                Logger.Info($"New version found (commit {shortSha}): {changed.Count} file(s) changed.");

                await ApplyUpdateAsync(s, sha, changed, ct);

                _lastVerifiedKey = key;
                s.LastUpdateTime = DateTime.Now;
                var done = $"Updated {changed.Count} file(s) to commit {shortSha}";
                Logger.Success(done + ".");
                Report(new UpdateProgress(UpdateStage.Completed, done, changed.Count, changed.Count));
                return UpdateResult.Updated;
            }
            catch (Exception ex)
            {
                var msg = ex is OperationCanceledException ? "Cancelled." : ex.Message;
                Logger.Error(msg);
                Report(new UpdateProgress(UpdateStage.Failed, msg));
                return UpdateResult.Failed;
            }
            finally { _gate.Release(); }
        }

        private async Task ApplyUpdateAsync(AppSettings s, string sha, List<(RemoteFile Remote, string LocalPath)> changed, CancellationToken ct)
        {
            // 1) Download everything to a staging folder and verify it (the program keeps running meanwhile)
            var staging = Path.Combine(AppSettings.DataFolder, "downloads", sha[..7]);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);

            long total = Math.Max(1, changed.Sum(c => c.Remote.Size));
            long doneBefore = 0;
            var lastReport = DateTime.MinValue;
            for (int i = 0; i < changed.Count; i++)
            {
                var remote = changed[i].Remote;
                int index = i + 1;
                Report(new UpdateProgress(UpdateStage.Downloading, "Downloading update...", index, changed.Count, doneBefore, total, remote.RelativePath));

                var dest = StagingPath(staging, remote);
                await _github.DownloadAsync(s, sha, remote, dest, bytes =>
                {
                    if ((DateTime.Now - lastReport).TotalMilliseconds < 80) return;   // throttle UI updates
                    lastReport = DateTime.Now;
                    Report(new UpdateProgress(UpdateStage.Downloading, "Downloading update...", index, changed.Count,
                        doneBefore + Math.Min(bytes, remote.Size), total, remote.RelativePath));
                }, ct);
                doneBefore += remote.Size;

                if (ComputeGitBlobSha(dest) != remote.BlobSha)
                {
                    if (IsLfsPointer(dest))
                        throw new InvalidOperationException($"'{remote.RepoPath}' is stored with Git LFS; LFS files are not supported.");
                    throw new InvalidOperationException($"Downloaded file '{remote.RepoPath}' failed verification (corrupt download).");
                }
                Logger.Info($"Downloaded {remote.RelativePath} ({FormatSize(remote.Size)}).");
            }
            Report(new UpdateProgress(UpdateStage.Downloading, "Download complete", changed.Count, changed.Count, total, total));
            Logger.Info($"{changed.Count} file(s) downloaded and verified.");

            // 2) Close the program
            string? exePath = string.IsNullOrWhiteSpace(s.ExeName) ? null : Path.Combine(Path.GetFullPath(s.LocalFolder), s.ExeName);
            bool wasRunning = false;
            if (exePath != null)
            {
                Report(new UpdateProgress(UpdateStage.Closing, $"Closing {Path.GetFileName(exePath)}..."));
                wasRunning = await CloseProcessAsync(exePath);
            }

            // 3) Replace files (roll back from backup on failure)
            var backup = Path.Combine(AppSettings.DataFolder, "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var replaced = new List<(string Local, string? Backup)>();
            try
            {
                Report(new UpdateProgress(UpdateStage.Installing, "Installing update..."));
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
                Logger.Info($"{replaced.Count} file(s) replaced in {s.LocalFolder}.");
            }
            catch (Exception ex)
            {
                Logger.Error("Replacing files failed, rolling back to the previous version: " + ex.Message);
                foreach (var (local, bak) in replaced)
                {
                    try { if (bak != null) File.Copy(bak, local, true); else File.Delete(local); } catch { }
                }
                if (exePath != null && wasRunning) StartProgram(exePath);
                throw;
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
            }

            if (!s.KeepBackup) { try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { } }
            else if (Directory.Exists(backup))
            {
                Logger.Info($"Backup of replaced files: {backup}");
                CleanupOldBackups(keep: 3);
            }

            // 4) Restart
            if (exePath != null && s.RestartAfterUpdate)
            {
                Report(new UpdateProgress(UpdateStage.Starting, $"Starting {Path.GetFileName(exePath)}..."));
                StartProgram(exePath);
            }
        }

        private static string StagingPath(string staging, RemoteFile f) =>
            Path.Combine(staging, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Closes the exe gracefully, then forcefully if needed. Returns true if it was running.</summary>
        public static async Task<bool> CloseProcessAsync(string exePath)
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            var procs = Process.GetProcessesByName(name).Where(p => p.Id != Environment.ProcessId && IsSameExe(p, exePath)).ToList();
            if (procs.Count == 0) { Logger.Info($"{name} is not running."); return false; }

            Logger.Info($"Closing {name} ({procs.Count} process(es))...");
            foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }

            for (int t = 0; t < 50 && procs.Any(p => !HasExited(p)); t++) await Task.Delay(100);

            foreach (var p in procs.Where(p => !HasExited(p)))
            {
                try { p.Kill(entireProcessTree: true); Logger.Warn($"{name} did not close in time and was terminated."); }
                catch (Win32Exception ex) { throw new InvalidOperationException($"Could not close {name}: {ex.Message}"); }
            }
            foreach (var p in procs) { try { p.WaitForExit(10000); } catch { } p.Dispose(); }
            await Task.Delay(500); // give Windows time to release file locks
            return true;
        }

        private static bool HasExited(Process p) { try { return p.HasExited; } catch { return true; } }

        private static bool IsSameExe(Process p, string exePath)
        {
            try
            {
                var path = p.MainModule?.FileName;
                return path == null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; } // path unreadable: treat a same-named process as the target
        }

        public static void StartProgram(string exePath)
        {
            if (!File.Exists(exePath)) { Logger.Warn($"Cannot start, file not found: {exePath}"); return; }
            try
            {
                Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)!
                });
                Logger.Info($"Started {Path.GetFileName(exePath)}.");
            }
            catch (Exception ex) { Logger.Error("Could not start the program: " + ex.Message); }
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

        private static void CleanupOldBackups(int keep)
        {
            try
            {
                var root = new DirectoryInfo(Path.Combine(AppSettings.DataFolder, "backups"));
                if (!root.Exists) return;
                foreach (var d in root.GetDirectories().OrderByDescending(d => d.Name).Skip(keep))
                    d.Delete(true);
            }
            catch { }
        }

        /// <summary>Git's file identity: SHA1("blob {size}\0" + content).</summary>
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

        private void Report(UpdateProgress p) => Progress?.Invoke(p);

        public void Dispose() { _github.Dispose(); _gate.Dispose(); }
    }
}
