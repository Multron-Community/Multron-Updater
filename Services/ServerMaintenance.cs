using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    /// <summary>
    /// Multron / VirusKov cloud scan server: before the updater closes it, the server is
    /// switched to its "updating server" maintenance mode through its local dashboard, so
    /// clients are told the server is being updated, and scans in progress may finish.
    /// The mode is not saved by the server, so the updated server starts without it
    /// (a normal maintenance mode that was on before stays on).
    /// </summary>
    public static class ServerMaintenance
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>The server's mode before the update ("off" or "maintenance"); null = not reached.</summary>
        public sealed record Handle(string BaseUrl, string PreviousMode);

        private static string BaseUrl(UpdateProfile p)
        {
            var url = string.IsNullOrWhiteSpace(p.ServerDashboardUrl) ? "http://127.0.0.1:9440" : p.ServerDashboardUrl.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;
            return url.TrimEnd('/');
        }

        private static async Task<JsonElement> GetStatusAsync(string baseUrl, CancellationToken ct)
        {
            using var res = await Http.GetAsync(baseUrl + "/api/maintenance", ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.Clone();
        }

        private static async Task PostAsync(string baseUrl, bool enabled, string mode, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/maintenance")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { enabled, mode }), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Multron-Dashboard", "1");
            using var res = await Http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Switches the server to "updating server" maintenance and waits (up to the
        /// profile's limit) until no scan is running. Null when the server could not be
        /// reached (not running): the update goes on as usual.
        /// </summary>
        public static async Task<Handle?> EnterUpdatingAsync(UpdateProfile p, CancellationToken ct)
        {
            var name = p.DisplayName;
            var baseUrl = BaseUrl(p);
            string previous;
            try
            {
                var status = await GetStatusAsync(baseUrl, ct);
                previous = status.TryGetProperty("mode", out var m) ? m.GetString() ?? "off" : "off";
                if (previous == "updating") previous = "off";
                await PostAsync(baseUrl, true, "updating", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Logger.Warn($"[{name}] Could not switch the server to maintenance at {baseUrl} ({ex.Message}); updating without it.");
                return null;
            }
            Logger.Info($"[{name}] Server switched to maintenance (\"updating server\"); new scans are refused with that message.");

            var limit = TimeSpan.FromSeconds(Math.Clamp(p.ServerDrainSeconds, 0, 600));
            var started = DateTime.UtcNow;
            int active = -1;
            while (DateTime.UtcNow - started < limit)
            {
                try
                {
                    var status = await GetStatusAsync(baseUrl, ct);
                    active = status.TryGetProperty("activeWork", out var a) ? a.GetInt32() : 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    active = -1;
                    break;
                }
                if (active == 0) break;
                await Task.Delay(1000, ct);
            }
            if (active > 0)
                Logger.Warn($"[{name}] {active} scan(s) still running after {limit.TotalSeconds:0} s; closing the server anyway.");
            else if (active == 0)
                Logger.Info($"[{name}] No scans running; closing the server.");
            return new Handle(baseUrl, previous);
        }

        /// <summary>True when the server's dashboard answers (the server is running).</summary>
        public static async Task<bool> IsRunningAsync(UpdateProfile p, CancellationToken ct)
        {
            try { await GetStatusAsync(BaseUrl(p), ct); return true; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return false; }
        }

        /// <summary>
        /// Asks the running server to reload its signatures and rules from disk
        /// (POST /api/engine/reload; the verdict cache is cleared). When a reload is
        /// already running, it waits for it and asks again (up to 2 minutes).
        /// </summary>
        public static async Task<bool> ReloadSignaturesAsync(UpdateProfile p, CancellationToken ct)
        {
            var name = p.DisplayName;
            var url = BaseUrl(p) + "/api/engine/reload";
            var started = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("", Encoding.UTF8, "application/json") };
                    req.Headers.Add("X-Multron-Dashboard", "1");
                    using var res = await Http.SendAsync(req, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        Logger.Info($"[{name}] Server signature reload started; the server was not restarted.");
                        return true;
                    }
                    var body = await res.Content.ReadAsStringAsync(ct);
                    if ((int)res.StatusCode == 409 && DateTime.UtcNow - started < TimeSpan.FromMinutes(2))
                    {
                        await Task.Delay(3000, ct);
                        continue;
                    }
                    Logger.Warn($"[{name}] The server refused the signature reload ({(int)res.StatusCode}: {body.Trim()}).");
                    return false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Logger.Warn($"[{name}] Could not ask the server to reload its signatures ({ex.Message}).");
                    return false;
                }
            }
        }

        /// <summary>
        /// Puts the server back into its previous mode when it was not closed after all
        /// (the update stopped before the program was closed).
        /// </summary>
        public static async Task RestoreAsync(UpdateProfile p, Handle? h)
        {
            if (h == null) return;
            try
            {
                await PostAsync(h.BaseUrl, h.PreviousMode == "maintenance", h.PreviousMode == "maintenance" ? "maintenance" : "off", CancellationToken.None);
                Logger.Info($"[{p.DisplayName}] Server maintenance set back to \"{h.PreviousMode}\".");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[{p.DisplayName}] Could not set the server's maintenance mode back ({ex.Message}); turn it off in the server dashboard.");
            }
        }
    }
}
