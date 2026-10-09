using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    public sealed record RemoteFile(string RepoPath, string RelativePath, string BlobSha, long Size);

    public sealed record ReleaseAsset(string Name, string DownloadUrl, string ApiUrl, long Size, string? Sha256, string UpdatedAt);

    public sealed record ReleaseData(string Tag, string Name, List<ReleaseAsset> Assets);

    public sealed class GitHubClient : IDisposable
    {
        public static string WebBase { get; set; } = "https://github.com";
        public static string RawBase { get; set; } = "https://raw.githubusercontent.com";
        public static string ApiBase { get; set; } = "https://api.github.com";

        private readonly HttpClient _http;
        private readonly HttpClient _noRedirect;
        private readonly ConcurrentDictionary<string, (string ETag, string Sha)> _commitCache = new();

        public GitHubClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MultronUpdater/1.0");
            _noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            _noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("MultronUpdater/1.0");
        }

        public async Task<string?> GetLatestReleaseTagAsync(UpdateProfile p, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(p.GetToken())) return null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, $"{WebBase}/{Esc(p.Owner)}/{Esc(p.Repo)}/releases/latest");
                using var res = await _noRedirect.SendAsync(req, ct);
                var location = res.Headers.Location?.ToString();
                if ((int)res.StatusCode is < 300 or >= 400 || location == null) return null;
                var marker = "/releases/tag/";
                var i = location.IndexOf(marker, StringComparison.Ordinal);
                return i < 0 ? "" : Uri.UnescapeDataString(location[(i + marker.Length)..]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        public async Task<ReleaseData?> GetLatestReleaseAsync(UpdateProfile p, CancellationToken ct)
        {
            using var req = ApiRequest($"{ApiBase}/repos/{Esc(p.Owner)}/{Esc(p.Repo)}/releases/latest", "application/vnd.github+json", p.GetToken());
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                using var repoReq = ApiRequest($"{ApiBase}/repos/{Esc(p.Owner)}/{Esc(p.Repo)}", "application/vnd.github+json", p.GetToken());
                using var repoRes = await _http.SendAsync(repoReq, ct);
                if (repoRes.IsSuccessStatusCode) return null;
                await EnsureOk(repoRes, "repository info");
            }
            await EnsureOk(res, "the latest release");

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            var assets = new List<ReleaseAsset>();
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string? sha = null;
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String &&
                    d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].ToLowerInvariant();
                assets.Add(new ReleaseAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.GetProperty("url").GetString() ?? "",
                    a.GetProperty("size").GetInt64(),
                    sha,
                    a.TryGetProperty("updated_at", out var u) ? u.GetString() ?? "" : ""));
            }
            return new ReleaseData(
                root.GetProperty("tag_name").GetString() ?? "",
                root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                assets);
        }

        public async Task DownloadAssetAsync(UpdateProfile p, ReleaseAsset asset, string destFile, Action<long>? onBytes, CancellationToken ct)
        {
            var token = p.GetToken();
            HttpRequestMessage req;
            if (string.IsNullOrWhiteSpace(token))
                req = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            else
            {
                req = new HttpRequestMessage(HttpMethod.Get, asset.ApiUrl);
                req.Headers.Accept.ParseAdd("application/octet-stream");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using (req)
            {
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                await EnsureOk(res, $"download of release asset '{asset.Name}'");
                await CopyToFileAsync(res, destFile, onBytes, ct);
            }
        }

        public async Task<string?> TryGetRawTextAsync(UpdateProfile p, string commitSha, string repoPath, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{RawBase}/{Esc(p.Owner)}/{Esc(p.Repo)}/{commitSha}/{EscPath(repoPath)}");
            var token = p.GetToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct);
        }

        private static async Task CopyToFileAsync(HttpResponseMessage res, string destFile, Action<long>? onBytes, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destFile);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                onBytes?.Invoke(total);
            }
        }

        private static HttpRequestMessage ApiRequest(string url, string accept, string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd(accept);
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        private static string Esc(string s) => Uri.EscapeDataString(s);

        internal static string EscPath(string path) =>
            string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        public async Task<string> GetLatestCommitShaAsync(UpdateProfile p, CancellationToken ct)
        {
            try
            {
                var sha = await GetCommitShaViaGitAsync(p, ct);
                if (sha != null) return sha;
            }
            catch (GitRefException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { }
            return await GetCommitShaViaApiAsync(p, ct);
        }

        private sealed class GitRefException : InvalidOperationException
        {
            public GitRefException(string message) : base(message) { }
        }

        private async Task<string?> GetCommitShaViaGitAsync(UpdateProfile p, CancellationToken ct)
        {
            static string Pkt(string s) => $"{s.Length + 4:x4}{s}";
            var branch = p.Branch.Trim();
            var body = Pkt("command=ls-refs\n") + "0001" + Pkt("peel\n") +
                       Pkt($"ref-prefix refs/heads/{branch}\n") + Pkt($"ref-prefix refs/tags/{branch}\n") + "0000";

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/{Esc(p.Owner)}/{Esc(p.Repo)}.git/git-upload-pack")
            {
                Content = new StringContent(body, Encoding.ASCII)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");
            req.Headers.Accept.ParseAdd("application/x-git-upload-pack-result");
            req.Headers.Add("Git-Protocol", "version=2");
            var token = p.GetToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + token.Trim())));

            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
                throw new GitRefException(string.IsNullOrWhiteSpace(token)
                    ? $"Repository '{p.Owner}/{p.Repo}' was not found (or it is private and needs an access token)."
                    : $"Repository '{p.Owner}/{p.Repo}' was not found, or the access token cannot read it.");
            if (!res.IsSuccessStatusCode) return null;

            var data = await res.Content.ReadAsByteArrayAsync(ct);
            string? headSha = null, tagSha = null;
            int pos = 0;
            while (pos + 4 <= data.Length)
            {
                int len = Convert.ToInt32(Encoding.ASCII.GetString(data, pos, 4), 16);
                if (len <= 2) { pos += 4; continue; }
                if (pos + len > data.Length) break;
                var line = Encoding.UTF8.GetString(data, pos + 4, len - 4).TrimEnd('\n');
                pos += len;

                var parts = line.Split(' ');
                if (parts.Length < 2 || parts[0].Length != 40) continue;
                var peeled = parts.Skip(2).FirstOrDefault(x => x.StartsWith("peeled:"))?[7..];
                if (parts[1] == $"refs/heads/{branch}") headSha = parts[0];
                else if (parts[1] == $"refs/tags/{branch}") tagSha = peeled ?? parts[0];
            }

            var sha = headSha ?? tagSha;
            if (sha != null) return sha;
            if (branch.Length is >= 7 and <= 40 && branch.All(Uri.IsHexDigit)) return null;
            throw new GitRefException($"Branch '{branch}' was not found in {p.Owner}/{p.Repo}.");
        }

        private async Task<string> GetCommitShaViaApiAsync(UpdateProfile p, CancellationToken ct)
        {
            var url = $"{ApiBase}/repos/{Esc(p.Owner)}/{Esc(p.Repo)}/commits/{Esc(p.Branch)}";
            var key = url + "|" + p.EncryptedToken;
            using var req = ApiRequest(url, "application/vnd.github.sha", p.GetToken());
            if (_commitCache.TryGetValue(key, out var cached))
                req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotModified && cached.Sha != null)
                return cached.Sha;

            await EnsureOk(res, "commit info");
            var sha = (await res.Content.ReadAsStringAsync(ct)).Trim();
            var etag = res.Headers.ETag?.ToString();
            if (etag != null) _commitCache[key] = (etag, sha);
            return sha;
        }

        public async Task<List<RemoteFile>> GetFilesAsync(UpdateProfile p, string commitSha, CancellationToken ct)
        {
            var url = $"{ApiBase}/repos/{Esc(p.Owner)}/{Esc(p.Repo)}/git/trees/{Esc(commitSha)}?recursive=1";
            using var req = ApiRequest(url, "application/vnd.github+json", p.GetToken());
            using var res = await _http.SendAsync(req, ct);
            await EnsureOk(res, "file list");

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("truncated", out var tr) && tr.GetBoolean())
                Logger.Warn($"[{p.DisplayName}] The repository is very large; GitHub truncated the file list. Some files may be missing.");

            var target = p.RepoPath.Replace('\\', '/').Trim('/');
            var result = new List<RemoteFile>();
            foreach (var item in root.GetProperty("tree").EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "blob") continue;
                var path = item.GetProperty("path").GetString()!;
                var sha = item.GetProperty("sha").GetString()!;
                var size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

                if (string.Equals(path, target, StringComparison.Ordinal))
                    result.Add(new RemoteFile(path, Path.GetFileName(path), sha, size));
                else if (path.StartsWith(target + "/", StringComparison.Ordinal))
                    result.Add(new RemoteFile(path, path[(target.Length + 1)..], sha, size));
            }
            return result;
        }

        public async Task DownloadAsync(UpdateProfile p, string commitSha, RemoteFile f, string destFile,
                                        Action<long>? onBytes, CancellationToken ct)
        {
            var url = $"{RawBase}/{Esc(p.Owner)}/{Esc(p.Repo)}/{commitSha}/{EscPath(f.RepoPath)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var token = p.GetToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("token", token);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureOk(res, $"download of '{f.RepoPath}'");
            await CopyToFileAsync(res, destFile, onBytes, ct);
        }

        private static async Task EnsureOk(HttpResponseMessage res, string what)
        {
            if (res.IsSuccessStatusCode) return;
            var code = (int)res.StatusCode;
            bool rateLimited = (code == 403 || code == 429) &&
                               res.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0";
            string msg = code switch
            {
                404 => "not found (owner/repo/branch/path may be wrong, or the repo is private and needs a token)",
                401 => "unauthorized (the access token is invalid or expired)",
                422 => "the branch, tag or commit was not found",
                _ when rateLimited => "GitHub rate limit reached" + ResetText(res) + ". Add an access token to raise the limit to 5000/hour",
                _ => $"HTTP {code} {res.ReasonPhrase}"
            };
            if (code is not (404 or 401 or 422) && !rateLimited)
            {
                try
                {
                    var body = await res.Content.ReadAsStringAsync();
                    if (body.Length > 200) body = body[..200];
                    msg += " " + body;
                }
                catch { }
            }
            throw new InvalidOperationException($"Could not get {what} from GitHub: {msg}.");
        }

        private static string ResetText(HttpResponseMessage res)
        {
            if (res.Headers.TryGetValues("X-RateLimit-Reset", out var v) && long.TryParse(v.FirstOrDefault(), out var unix))
                return $" (resets at {DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime():HH:mm})";
            return "";
        }

        public void Dispose() { _http.Dispose(); _noRedirect.Dispose(); }
    }
}
