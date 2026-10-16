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

    /// <summary>
    /// Where updates come from. <see cref="LocalFolder"/> reads a copy of the repository on
    /// disk (a clone, a USB drive, a network share) and never uses the internet. New values
    /// go at the end: settings.json stores the number.
    /// </summary>
    public enum BuildShell { Cmd, WindowsPowerShell, PowerShell7 }

    public enum TargetSource { Both, Repository, Releases, LocalFolder }

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

        public TargetSource Source { get; set; } = TargetSource.Both;
        public string ReleaseAsset { get; set; } = "";
        public bool ExtractZipAssets { get; set; } = true;
        public string? InstalledReleaseTag { get; set; }
        public string? InstalledReleaseDigest { get; set; }
        public List<string> InstalledReleaseFiles { get; set; } = new();

        /// <summary>Root of the repository copy on disk (for <see cref="TargetSource.LocalFolder"/>).</summary>
        public string LocalRepoFolder { get; set; } = "";

        [JsonIgnore] public bool UsesRepository => Source is TargetSource.Both or TargetSource.Repository;
        [JsonIgnore] public bool UsesReleases => Source is TargetSource.Both or TargetSource.Releases;
        [JsonIgnore] public bool UsesLocalFolder => Source == TargetSource.LocalFolder;

        public bool BuildEnabled { get; set; } = false;
        public string BuildSourceFolder { get; set; } = "";
        public BuildShell BuildShell { get; set; } = BuildShell.Cmd;
        public string BuildCommands { get; set; } = "";
        public string BuildWorkingDir { get; set; } = "";
        public string BuildOutputPath { get; set; } = "";
        public string BuildEnvironment { get; set; } = "";
        public int BuildTimeoutMinutes { get; set; } = 30;
        public bool BuildStopOnError { get; set; } = true;
        public bool BuildCleanOutput { get; set; } = true;
        public string? BuildLastSuccessKey { get; set; }

        [JsonIgnore] public string EffectiveBuildSourceFolder => string.IsNullOrWhiteSpace(BuildSourceFolder)
            ? System.IO.Path.Combine(AppSettings.DataFolder, "sources", Id)
            : System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(BuildSourceFolder.Trim()));

        public string LocalFolder { get; set; } = "";
        public string ExeName { get; set; } = "";
        public bool IsConsoleApp { get; set; } = false;
        public string StartArguments { get; set; } = "";
        public bool HideConsole { get; set; } = false;
        public bool KeepConsoleOpen { get; set; } = false;
        public bool StartWithoutAdmin { get; set; } = false;
        public bool RestartAfterUpdate { get; set; } = true;
        public bool RefreshEdgeAfterUpdate { get; set; } = false;
        public bool KeepBackup { get; set; } = true;

        /// <summary>Multron cloud scan server: switch it to "updating server" maintenance before closing it.</summary>
        public bool ServerMaintenance { get; set; } = false;
        public bool? ServerUseGlobal { get; set; }
        public string ServerDashboardUrl { get; set; } = AppSettings.DefaultServerUrl;
        /// <summary>How long to wait for scans in progress before closing the server.</summary>
        public int ServerDrainSeconds { get; set; } = 60;

        [JsonIgnore] public string EffectiveServerUrl =>
            ServerUseGlobal != false ? AppSettings.Current.ServerDashboardUrl : ServerDashboardUrl;
        [JsonIgnore] public int EffectiveServerDrainSeconds =>
            ServerUseGlobal != false ? AppSettings.Current.ServerDrainSeconds : ServerDrainSeconds;
        /// <summary>
        /// Multron cloud scan server: when an update changes no program files (.exe / .dll),
        /// the files are replaced while the server keeps running and its signatures are
        /// reloaded through the dashboard instead of restarting it.
        /// </summary>
        public bool ServerReloadSignatures { get; set; } = false;
        /// <summary>
        /// Multron cloud scan server: seamless update. The running exe is renamed (Windows
        /// allows that), the new files are put in place and the new version is started with
        /// <c>--handoff</c> next to the running server, which keeps scanning ("getting
        /// updated") until the new one has loaded and takes its ports over in a few seconds.
        /// A server too old for it is updated the usual way once.
        /// </summary>
        public bool ServerSeamlessUpdate { get; set; } = false;
        /// <summary>How long the new version may take to load before the update is undone.</summary>
        public int ServerSeamlessMinutes { get; set; } = 20;

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
        public string SourceText => UsesLocalFolder
            ? (string.IsNullOrWhiteSpace(LocalRepoFolder) ? "Not configured" : $"Local: {LocalRepoFolder}")
            : string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(Repo)
                ? "Not configured"
                : $"{Owner}/{Repo} · {Branch}";

        [JsonIgnore]
        public string SourceKey => $"{Owner}/{Repo}@{Branch}:{RepoPath}>{LocalFolder}|{Source}|{ReleaseAsset}|{ExtractZipAssets}|{LocalRepoFolder}|{(BuildEnabled ? EffectiveBuildSourceFolder : "")}".ToLowerInvariant();

        public string EffectiveAssetPattern()
        {
            if (!string.IsNullOrWhiteSpace(ReleaseAsset)) return ReleaseAsset.Trim();
            if (!string.IsNullOrWhiteSpace(ExeName)) return System.IO.Path.GetFileName(ExeName.Trim());
            var name = RepoPath.Replace('\\', '/').Trim('/');
            name = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
            return name.Contains('.') ? name : "";
        }

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
            if (UsesLocalFolder)
            {
                if (string.IsNullOrWhiteSpace(LocalRepoFolder)) { error = "Local repository folder is empty."; return false; }
                if (string.IsNullOrWhiteSpace(LocalFolder)) { error = "Local target folder is empty."; return false; }
                return BuildIsConfigured(out error);
            }
            if (string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(Repo)) { error = "GitHub owner/repository is empty."; return false; }
            if (UsesRepository && string.IsNullOrWhiteSpace(Branch)) { error = "Branch is empty."; return false; }
            if (UsesRepository && !BuildEnabled && string.IsNullOrWhiteSpace(RepoPath)) { error = "Path in repository is empty."; return false; }
            if (string.IsNullOrWhiteSpace(LocalFolder)) { error = "Local target folder is empty."; return false; }
            return BuildIsConfigured(out error);
        }

        private bool BuildIsConfigured(out string error)
        {
            error = "";
            if (!BuildEnabled) return true;
            if (Source == TargetSource.Releases) { error = "Building from source needs the repository or a local repository folder as the update source."; return false; }
            if (string.IsNullOrWhiteSpace(BuildCommands)) { error = "Build commands are empty."; return false; }
            if (string.IsNullOrWhiteSpace(BuildOutputPath)) { error = "Build output folder is empty."; return false; }
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
            copy.BuildLastSuccessKey = null;
            if (!string.IsNullOrWhiteSpace(copy.BuildSourceFolder)) copy.BuildSourceFolder = "";
            return copy;
        }
    }

    public class AppSettings
    {
        public List<UpdateProfile> Profiles { get; set; } = new();

        public const string DefaultServerUrl = "http://127.0.0.1:9440";

        [JsonIgnore] public static AppSettings Current { get; private set; } = new();

        public AppTheme Theme { get; set; } = AppTheme.System;

        public bool MultronDeveloperOptions { get; set; } = false;
        public string ServerDashboardUrl { get; set; } = DefaultServerUrl;
        public int ServerDrainSeconds { get; set; } = 60;

        public bool AutoUpdateEnabled { get; set; } = false;
        public int CheckIntervalMinutes { get; set; } = 5;
        public bool NotifyOnUpdate { get; set; } = true;

        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = true;

        public bool SelfUpdateEnabled { get; set; } = true;
        public DateTime? LastSelfUpdateCheck { get; set; }

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
            catch (Exception ex)
            {
                s = new AppSettings();
                try
                {
                    var bad = SettingsFile + ".bad-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(SettingsFile, bad, true);
                    Logger.Warn($"settings.json could not be read ({ex.Message}). It was saved as {Path.GetFileName(bad)} and default settings are used.");
                }
                catch { }
            }

            if (s.Profiles.Count == 0) s.Profiles.Add(new UpdateProfile { Name = "My program" });
            foreach (var p in s.Profiles.Where(p => p.ServerUseGlobal == null))
                p.ServerUseGlobal = string.Equals(p.ServerDashboardUrl.Trim(), s.ServerDashboardUrl.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                    p.ServerDrainSeconds == s.ServerDrainSeconds;
            Current = s;
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
