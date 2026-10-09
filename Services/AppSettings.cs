using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultronUpdater.Services
{
    /// <summary>All user-defined settings, stored in %AppData%\MultronUpdater\settings.json.</summary>
    public class AppSettings
    {
        // --- GitHub source ---
        public string Owner { get; set; } = "";
        public string Repo { get; set; } = "";
        public string Branch { get; set; } = "main";
        /// <summary>File or folder path inside the repo (e.g. "bin/Release/App.exe" or "bin/Release").</summary>
        public string RepoPath { get; set; } = "";
        /// <summary>Token encrypted with DPAPI (only this Windows user can decrypt it).</summary>
        public string? EncryptedToken { get; set; }

        // --- Local target ---
        public string LocalFolder { get; set; } = "";
        /// <summary>Executable to close and restart, relative to LocalFolder (e.g. "App.exe").</summary>
        public string ExeName { get; set; } = "";
        public bool RestartAfterUpdate { get; set; } = true;
        public bool KeepBackup { get; set; } = true;

        // --- Automatic updates ---
        public bool AutoUpdateEnabled { get; set; } = false;
        public int CheckIntervalMinutes { get; set; } = 5;
        public bool NotifyOnUpdate { get; set; } = true;

        // --- Application ---
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = true;

        // --- State (maintained automatically) ---
        public string? LastCommitSha { get; set; }
        public DateTime? LastUpdateTime { get; set; }

        public string GetToken()
        {
            if (string.IsNullOrEmpty(EncryptedToken)) return "";
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(EncryptedToken), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch { return ""; }
        }

        public void SetToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) { EncryptedToken = null; return; }
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser);
            EncryptedToken = Convert.ToBase64String(data);
        }

        public bool IsConfigured(out string error)
        {
            if (string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(Repo)) { error = "GitHub owner/repository is empty."; return false; }
            if (string.IsNullOrWhiteSpace(Branch)) { error = "Branch is empty."; return false; }
            if (string.IsNullOrWhiteSpace(RepoPath)) { error = "Path in repository is empty."; return false; }
            if (string.IsNullOrWhiteSpace(LocalFolder)) { error = "Local target folder is empty."; return false; }
            error = "";
            return true;
        }

        /// <summary>Identifies the source/target pair, so a settings change forces a full re-check.</summary>
        public string SourceKey => $"{Owner}/{Repo}@{Branch}:{RepoPath}>{LocalFolder}".ToLowerInvariant();

        public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

        // ------------------------------------------------------------------
        public static string DataFolder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultronUpdater");

        private static string SettingsFile => Path.Combine(DataFolder, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOpts) ?? new AppSettings();
            }
            catch { /* corrupt file: continue with defaults */ }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(DataFolder);
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, SettingsFile, true);
        }
    }
}
