using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    public sealed record ReleaseInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string Notes, string PageUrl);

    public sealed class SelfUpdater : IDisposable
    {
        public const string Owner = "Multron-Community";
        public const string Repo = "Multron-Updater";
        public const string AssetName = "MultronUpdater.exe";
        public const string ProgressId = "self";
        public const string DisplayName = "Multron Updater";

        public static string ApiBase { get; set; } = "https://api.github.com";

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private string? _etag;
        private ReleaseInfo? _cached;

        public event Action<UpdateProgress>? Progress;

        public SelfUpdater()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MultronUpdater/" + CurrentVersion);
        }

        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string ExePath => Environment.ProcessPath ?? "";

        public static bool CanUpdateItself(out string reason)
        {
            var path = ExePath;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { reason = "The program path could not be determined."; return false; }
            if (path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            { reason = "This is a development build (running from the bin folder)."; return false; }
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "MultronUpdater.dll")))
            { reason = "This is not a single-file build."; return false; }
            reason = "";
            return true;
        }

        public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{Owner}/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);

            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotModified) return _cached;
            if (res.StatusCode == HttpStatusCode.NotFound) { _cached = null; return null; }
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Could not check for Multron Updater updates: HTTP {(int)res.StatusCode} {res.ReasonPhrase}.");

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                throw new InvalidOperationException($"The latest release tag '{tag}' is not a version number.");

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException($"The latest release {tag} has no {AssetName} file.");

            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var digest = d.GetString() ?? "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].ToLowerInvariant();
            }

            _cached = new ReleaseInfo(
                new Version(version.Major, version.Minor, Math.Max(0, version.Build)),
                tag,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                sha,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
            _etag = res.Headers.ETag?.ToString();
            return _cached;
        }

        public async Task InstallAsync(ReleaseInfo release, CancellationToken ct = default)
        {
            if (!CanUpdateItself(out var reason)) throw new InvalidOperationException("Cannot update Multron Updater: " + reason);

            var exe = ExePath;
            var folder = Path.Combine(AppSettings.DataFolder, "self-update");
            Directory.CreateDirectory(folder);
            var download = Path.Combine(folder, $"MultronUpdater-{release.Version}.exe");

            Report(UpdateStage.Downloading, "Downloading update...", 0, release.Size);
            using (var res = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Could not download Multron Updater {release.Version}: HTTP {(int)res.StatusCode}.");
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(download);
                var buffer = new byte[81920];
                long done = 0;
                var last = DateTime.MinValue;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if ((DateTime.Now - last).TotalMilliseconds > 80)
                    {
                        last = DateTime.Now;
                        Report(UpdateStage.Downloading, "Downloading update...", done, Math.Max(release.Size, done));
                    }
                }
            }

            Report(UpdateStage.Installing, "Verifying the download...");
            var info = new FileInfo(download);
            if (info.Length != release.Size)
                throw new InvalidOperationException($"The downloaded file has the wrong size ({info.Length} instead of {release.Size} bytes).");
            using (var fs = File.OpenRead(download))
            {
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                    throw new InvalidOperationException("The downloaded file is not a Windows program.");
            }
            if (release.Sha256 != null)
            {
                string actual;
                using (var fs = File.OpenRead(download)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != release.Sha256)
                    throw new InvalidOperationException("The downloaded file failed SHA-256 verification.");
            }

            Report(UpdateStage.Installing, "Installing update...");
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
            Logger.Success($"Multron Updater {release.Version} installed. Restarting...");
        }

        public static void Restart(bool hidden)
        {
            Process.Start(new ProcessStartInfo(ExePath, hidden ? "--updated --tray" : "--updated")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ExePath)!
            });
        }

        public static void CleanupOldVersion()
        {
            var old = ExePath + ".old";
            if (!File.Exists(old)) return;
            Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try { File.Delete(old); return; }
                    catch { await Task.Delay(1000); }
                }
            });
            try
            {
                var folder = Path.Combine(AppSettings.DataFolder, "self-update");
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch { }
        }

        private void Report(UpdateStage stage, string message, long done = 0, long total = 0) =>
            Progress?.Invoke(new UpdateProgress(ProgressId, DisplayName, stage, message, 1, 1, done, total, AssetName));

        public void Dispose() => _http.Dispose();
    }
}
