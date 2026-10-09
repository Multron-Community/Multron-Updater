using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultronUpdater.Services
{
    public class TrackedFile
    {
        public string Path { get; set; } = "";
        public bool Include { get; set; } = true;
    }

    public class FileGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Files";
        public bool Enabled { get; set; } = true;
        public bool RestartProgram { get; set; } = true;
        public bool RefreshEdge { get; set; } = false;
        public List<TrackedFile> Files { get; set; } = new();
    }

    public class UpdateProfile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyDisplayChanged()
        {
            foreach (var name in new[] { nameof(DisplayName), nameof(SourceText), nameof(Enabled), nameof(Status) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New target";
        public bool Enabled { get; set; } = true;

        public string Owner { get; set; } = "";
        public string Repo { get; set; } = "";
        public string Branch { get; set; } = "main";
        public string RepoPath { get; set; } = "";
        public string? EncryptedToken { get; set; }

        public string LocalFolder { get; set; } = "";
        public string ExeName { get; set; } = "";
        public bool RestartAfterUpdate { get; set; } = true;
        public bool RefreshEdgeAfterUpdate { get; set; } = false;
        public bool KeepBackup { get; set; } = true;

        public bool OnlySelectedFiles { get; set; } = false;
        public List<FileGroup> Groups { get; set; } = new();

        [JsonIgnore]
        public string SelectionKey => OnlySelectedFiles
            ? string.Join("|", GetIncludedFiles().Keys.Select(k => k.ToLowerInvariant()).OrderBy(x => x))
            : "*";

        public Dictionary<string, FileGroup> GetIncludedFiles()
        {
            var map = new Dictionary<string, FileGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Groups.Where(g => g.Enabled))
                foreach (var f in g.Files.Where(f => f.Include))
                    map.TryAdd(f.Path.Replace('\\', '/').Trim('/'), g);
            return map;
        }

        public IEnumerable<string> AllTrackedPaths() =>
            Groups.SelectMany(g => g.Files).Select(f => f.Path.Replace('\\', '/').Trim('/'));

        public string? LastCommitSha { get; set; }
        public DateTime? LastUpdateTime { get; set; }

        private string _status = "";

        [JsonIgnore]
        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Repo) ? "Unnamed target" : Repo) : Name;

        [JsonIgnore]
        public string SourceText => string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(Repo)
            ? "Not configured"
            : $"{Owner}/{Repo} · {Branch}";

        [JsonIgnore]
        public string SourceKey => $"{Owner}/{Repo}@{Branch}:{RepoPath}>{LocalFolder}".ToLowerInvariant();

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
            if (token.Trim() == GetToken()) return;
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

        public UpdateProfile Clone() => JsonSerializer.Deserialize<UpdateProfile>(JsonSerializer.Serialize(this))!;

        public UpdateProfile Duplicate()
        {
            var copy = Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            foreach (var g in copy.Groups) g.Id = Guid.NewGuid().ToString("N");
            copy.Name = DisplayName + " (copy)";
            copy.LastCommitSha = null;
            copy.LastUpdateTime = null;
            return copy;
        }
    }

    public class AppSettings
    {
        public List<UpdateProfile> Profiles { get; set; } = new();

        public bool AutoUpdateEnabled { get; set; } = false;
        public int CheckIntervalMinutes { get; set; } = 5;
        public bool NotifyOnUpdate { get; set; } = true;

        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = true;

        public bool HasConfiguredProfile()
        {
            foreach (var p in Profiles)
                if (p.Enabled && p.IsConfigured(out _)) return true;
            return false;
        }

        public static string DataFolder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultronUpdater");

        private static string SettingsFile => Path.Combine(DataFolder, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            AppSettings s;
            try
            {
                s = File.Exists(SettingsFile)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOpts) ?? new AppSettings()
                    : new AppSettings();
            }
            catch { s = new AppSettings(); }

            if (s.Profiles.Count == 0) s.Profiles.Add(new UpdateProfile { Name = "My program" });
            return s;
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
