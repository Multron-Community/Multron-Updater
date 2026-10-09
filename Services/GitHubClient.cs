using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    public sealed record RemoteFile(string RepoPath, string RelativePath, string BlobSha, long Size);

    public sealed class GitHubClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, (string ETag, string Sha)> _commitCache = new();

        public GitHubClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MultronUpdater/1.0");
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

        private static string EscPath(string path) =>
            string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        public async Task<string> GetLatestCommitShaAsync(UpdateProfile p, CancellationToken ct)
        {
            var url = $"https://api.github.com/repos/{Esc(p.Owner)}/{Esc(p.Repo)}/commits/{Esc(p.Branch)}";
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
            var url = $"https://api.github.com/repos/{Esc(p.Owner)}/{Esc(p.Repo)}/git/trees/{Esc(commitSha)}?recursive=1";
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
            var url = $"https://raw.githubusercontent.com/{Esc(p.Owner)}/{Esc(p.Repo)}/{commitSha}/{EscPath(f.RepoPath)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var token = p.GetToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("token", token);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureOk(res, $"download of '{f.RepoPath}'");
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
                _ when rateLimited => "GitHub rate limit reached" + ResetText(res) + ". Add an access token to raise the limit to 5000/hour",
                _ => $"HTTP {code} {res.ReasonPhrase}"
            };
            if (code is not (404 or 401) && !rateLimited)
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

        public void Dispose() => _http.Dispose();
    }
}
