using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MultronUpdater.Services;
using MultronUpdater.Views;

namespace MultronUpdater
{
    public partial class App : Application
    {
        private const string MutexName = @"Local\MultronUpdater_SingleInstance";
        private const string ShowEventName = @"Local\MultronUpdater_Show";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private TrayIcon? _tray;
        private MainWindow? _main;
        private DownloadToast? _toast;
        private bool _toastSuppressed;
        private readonly DispatcherTimer _autoTimer = new();
        private readonly DispatcherTimer _selfTimer = new() { Interval = TimeSpan.FromHours(3) };
        private bool _selfWarned;

        public SelfUpdater SelfUpdate { get; } = new();
        public ReleaseInfo? AvailableUpdate { get; private set; }
        public bool IsSelfUpdating { get; private set; }
        public string SelfUpdateStatus { get; private set; } = "";

        public static new App Current => (App)Application.Current;

        public AppSettings Settings { get; private set; } = new();
        public UpdateService Updater { get; } = new();
        public bool IsExiting { get; private set; }
        public bool IsChecking { get; private set; }
        public DateTime? LastCheck { get; private set; }
        public DateTime? NextCheck { get; private set; }

        public event Action<UpdateProgress>? ProgressChanged;
        public event Action? StateChanged;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool justUpdated = e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase);
            bool trayArg = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
            _mutex = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst && justUpdated)
            {
                try { isFirst = _mutex.WaitOne(TimeSpan.FromSeconds(20)); }
                catch (AbandonedMutexException) { isFirst = true; }
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            if (!isFirst)
            {
                _showEvent.Set();
                Shutdown();
                return;
            }
            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                {
                    if (IsExiting) return;
                    Dispatcher.BeginInvoke(ShowMainWindow);
                }
            }) { IsBackground = true, Name = "ShowListener" }.Start();

            DispatcherUnhandledException += (_, ex) => { Logger.Error("Unexpected error: " + ex.Exception.Message); ex.Handled = true; };
            TaskScheduler.UnobservedTaskException += (_, ex) => { Logger.Error("Background error: " + ex.Exception.GetBaseException().Message); ex.SetObserved(); };

            Settings = AppSettings.Load();
            Logger.Info($"Multron Updater {SelfUpdater.CurrentVersion} started ({Settings.Profiles.Count} target(s)).");
            SelfUpdater.CleanupOldVersion();

            System.Drawing.Icon appIcon;
            using (var s = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream)
                appIcon = new System.Drawing.Icon(s);

            _tray = new TrayIcon(appIcon, Settings.AutoUpdateEnabled);
            _tray.OpenRequested += ShowMainWindow;
            _tray.CheckRequested += () => _ = RunCheckAsync(null, force: false, manual: true);
            _tray.AutoUpdateToggled += on => SetAutoUpdate(on);
            _tray.OpenLogRequested += OpenLogFile;
            _tray.ExitRequested += ExitApp;

            Updater.Progress += p => Dispatcher.BeginInvoke(() => OnProgress(p));
            SelfUpdate.Progress += p => Dispatcher.BeginInvoke(() => OnProgress(p));

            _main = new MainWindow();
            bool launchedHidden = trayArg && (justUpdated || (Settings.StartMinimized && Settings.HasConfiguredProfile()));
            if (!launchedHidden) ShowMainWindow();

            _autoTimer.Tick += async (_, _) =>
            {
                NextCheck = DateTime.Now + _autoTimer.Interval;
                await RunCheckAsync(null, force: false, manual: false);
            };
            ApplyAutoUpdate(checkSoon: true);

            if (justUpdated)
            {
                Logger.Success($"Multron Updater was updated to version {SelfUpdater.CurrentVersion}.");
                _tray.ShowBalloon("Multron Updater", $"Updated to version {SelfUpdater.CurrentVersion}.");
            }

            _selfTimer.Tick += async (_, _) => await CheckSelfUpdateAsync(manual: false);
            _selfTimer.Start();
            var firstSelfCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            firstSelfCheck.Tick += async (_, _) => { firstSelfCheck.Stop(); await CheckSelfUpdateAsync(manual: false); };
            firstSelfCheck.Start();
        }

        public async Task CheckSelfUpdateAsync(bool manual)
        {
            if (IsSelfUpdating) return;
            if (manual) Logger.Info("Checking for Multron Updater updates...");
            SelfUpdateStatus = "Checking for updates...";
            StateChanged?.Invoke();
            try
            {
                var release = await Task.Run(() => SelfUpdate.GetLatestReleaseAsync());
                Settings.LastSelfUpdateCheck = DateTime.Now;
                SaveSettings();

                if (release == null)
                {
                    AvailableUpdate = null;
                    SelfUpdateStatus = "No release has been published yet.";
                    if (manual) Logger.Info("Multron Updater: no release has been published on GitHub yet.");
                }
                else if (release.Version > SelfUpdater.CurrentVersion)
                {
                    bool firstTime = AvailableUpdate?.Version != release.Version;
                    AvailableUpdate = release;
                    SelfUpdateStatus = $"Version {release.Version} is available.";
                    if (firstTime || manual) Logger.Info($"Multron Updater {release.Version} is available (you have {SelfUpdater.CurrentVersion}).");

                    if (!SelfUpdater.CanUpdateItself(out var reason))
                    {
                        SelfUpdateStatus += " " + reason;
                        if (!_selfWarned || manual) Logger.Warn("Multron Updater cannot update itself: " + reason);
                        _selfWarned = true;
                    }
                    else if (Settings.SelfUpdateEnabled && !manual)
                    {
                        await InstallSelfUpdateAsync();
                    }
                    else if (firstTime && !manual)
                    {
                        _tray?.ShowBalloon("Multron Updater", $"Version {release.Version} is available. Open Multron Updater to install it.");
                    }
                }
                else
                {
                    AvailableUpdate = null;
                    SelfUpdateStatus = $"Up to date (latest release {release.Version}).";
                    if (manual) Logger.Info($"Multron Updater is up to date ({SelfUpdater.CurrentVersion}).");
                }
            }
            catch (Exception ex)
            {
                SelfUpdateStatus = "Could not check: " + ex.Message;
                if (manual) Logger.Error(ex.Message); else Logger.Warn(ex.Message);
            }
            StateChanged?.Invoke();
        }

        public async Task InstallSelfUpdateAsync()
        {
            var release = AvailableUpdate;
            if (release == null || IsSelfUpdating) return;

            for (int i = 0; i < 300 && IsChecking; i++) await Task.Delay(2000);
            if (IsChecking) return;

            IsSelfUpdating = true;
            SelfUpdateStatus = $"Installing version {release.Version}...";
            StateChanged?.Invoke();
            Logger.Info($"Updating Multron Updater {SelfUpdater.CurrentVersion} → {release.Version}...");
            try
            {
                await Task.Run(() => SelfUpdate.InstallAsync(release));
                OnProgress(new UpdateProgress(SelfUpdater.ProgressId, SelfUpdater.DisplayName, UpdateStage.Completed,
                    $"Version {release.Version} installed, restarting..."));
                bool hidden = _main == null || !_main.IsVisible;
                SelfUpdater.Restart(hidden);
                ExitApp();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                SelfUpdateStatus = "Update failed: " + ex.Message;
                OnProgress(new UpdateProgress(SelfUpdater.ProgressId, SelfUpdater.DisplayName, UpdateStage.Failed, ex.Message));
                IsSelfUpdating = false;
                StateChanged?.Invoke();
            }
        }

        public void ApplyAutoUpdate(bool checkSoon)
        {
            _autoTimer.Stop();
            _tray?.SetAutoUpdate(Settings.AutoUpdateEnabled);

            if (Settings.AutoUpdateEnabled && Settings.HasConfiguredProfile())
            {
                var minutes = Math.Clamp(Settings.CheckIntervalMinutes, 1, 1440);
                _autoTimer.Interval = TimeSpan.FromMinutes(minutes);
                _autoTimer.Start();
                NextCheck = DateTime.Now + _autoTimer.Interval;
                _tray?.SetTooltip($"Automatic updates on (every {minutes} min)");

                if (checkSoon)
                {
                    var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    once.Tick += async (_, _) => { once.Stop(); await RunCheckAsync(null, force: false, manual: false); };
                    once.Start();
                }
            }
            else
            {
                NextCheck = null;
                _tray?.SetTooltip(Settings.AutoUpdateEnabled ? "No configured target" : "Automatic updates off");
            }
            StateChanged?.Invoke();
        }

        public void SetAutoUpdate(bool enabled)
        {
            if (Settings.AutoUpdateEnabled == enabled) return;
            Settings.AutoUpdateEnabled = enabled;
            SaveSettings();
            Logger.Info(enabled
                ? $"Automatic updates turned ON (every {Math.Clamp(Settings.CheckIntervalMinutes, 1, 1440)} min)."
                : "Automatic updates turned OFF.");
            ApplyAutoUpdate(checkSoon: enabled);
        }

        public void SaveSettings()
        {
            try { Settings.Save(); }
            catch (Exception ex) { Logger.Error("Could not save settings: " + ex.Message); }
        }

        public async Task<UpdateResult> RunCheckAsync(string? profileId, bool force, bool manual)
        {
            if (IsChecking)
            {
                if (manual) Logger.Warn("An update check is already running.");
                return UpdateResult.Busy;
            }

            List<UpdateProfile> targets = profileId == null
                ? Settings.Profiles.Where(p => p.Enabled && p.IsConfigured(out _)).ToList()
                : Settings.Profiles.Where(p => p.Id == profileId).ToList();

            if (targets.Count == 0)
            {
                if (manual) Logger.Warn("There is no enabled, fully configured target to check.");
                return UpdateResult.Failed;
            }
            if (targets.Count == 1 && !targets[0].IsConfigured(out var err))
            {
                if (manual) Logger.Warn($"[{targets[0].DisplayName}] Cannot check: {err}");
                return UpdateResult.Failed;
            }

            if (manual)
                Logger.Info(force
                    ? $"Force update started for {targets[0].DisplayName}."
                    : $"Update check started for {(targets.Count == 1 ? targets[0].DisplayName : $"{targets.Count} targets")}.");

            IsChecking = true;
            StateChanged?.Invoke();
            var overall = UpdateResult.UpToDate;
            var updatedNames = new List<string>();
            try
            {
                foreach (var target in targets)
                {
                    var snapshot = target.Clone();
                    var result = await Task.Run(() => Updater.CheckAndUpdateAsync(snapshot, force));

                    var live = Settings.Profiles.FirstOrDefault(p => p.Id == target.Id);
                    if (live != null)
                    {
                        if (snapshot.LastCommitSha != null) live.LastCommitSha = snapshot.LastCommitSha;
                        if (snapshot.LastUpdateTime != null) live.LastUpdateTime = snapshot.LastUpdateTime;
                        live.InstalledReleaseTag = snapshot.InstalledReleaseTag;
                        live.InstalledReleaseDigest = snapshot.InstalledReleaseDigest;
                        live.InstalledReleaseFiles = snapshot.InstalledReleaseFiles;
                    }
                    if (result == UpdateResult.Updated) updatedNames.Add(target.DisplayName);
                    if (result == UpdateResult.Failed) overall = UpdateResult.Failed;
                    else if (result == UpdateResult.Updated && overall != UpdateResult.Failed) overall = UpdateResult.Updated;
                }
            }
            finally
            {
                IsChecking = false;
                LastCheck = DateTime.Now;
                SaveSettings();
            }

            if (updatedNames.Count > 0 && Settings.NotifyOnUpdate && _toast == null)
                _tray?.ShowBalloon("Multron Updater", "Updated from GitHub: " + string.Join(", ", updatedNames));

            StateChanged?.Invoke();
            return overall;
        }

        private void OnProgress(UpdateProgress p)
        {
            var profile = Settings.Profiles.FirstOrDefault(x => x.Id == p.ProfileId);
            if (profile != null)
            {
                profile.Status = p.Stage == UpdateStage.Downloading && p.BytesTotal > 0
                    ? $"Downloading {p.BytesDone * 100 / p.BytesTotal}%"
                    : p.Message;
            }

            ProgressChanged?.Invoke(p);

            switch (p.Stage)
            {
                case UpdateStage.Downloading:
                case UpdateStage.Closing:
                case UpdateStage.Installing:
                case UpdateStage.Starting:
                    _tray?.StartAnimation();
                    _tray?.SetTooltip(p.Stage == UpdateStage.Downloading && p.BytesTotal > 0
                        ? $"{p.ProfileName}: downloading {p.BytesDone * 100 / p.BytesTotal}%"
                        : $"{p.ProfileName}: {p.Message}");
                    if (Settings.NotifyOnUpdate && !_toastSuppressed)
                    {
                        if (_toast == null || _toast.IsClosing)
                        {
                            var toast = new DownloadToast();
                            toast.Closed += (_, _) =>
                            {
                                if (!toast.IsFinished) _toastSuppressed = true;
                                if (_toast == toast) _toast = null;
                            };
                            toast.Clicked += ShowMainWindow;
                            _toast = toast;
                            toast.Show();
                        }
                        _toast.Update(p);
                    }
                    break;

                case UpdateStage.Completed:
                    _tray?.StopAnimation();
                    _tray?.SetTooltip($"{p.ProfileName}: {p.Message}");
                    _toast?.ShowCompleted(p.ProfileName, p.Message);
                    _toastSuppressed = false;
                    break;

                case UpdateStage.Failed:
                    _tray?.StopAnimation();
                    _tray?.SetTooltip($"{p.ProfileName}: last check failed");
                    _toast?.ShowFailed(p.ProfileName, p.Message);
                    _toastSuppressed = false;
                    break;
            }
        }

        public void ShowMainWindow()
        {
            if (_main == null || IsExiting) return;
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;
            _main.Topmost = false;
            _main.Focus();
        }

        public void OpenLogFile()
        {
            try
            {
                if (!File.Exists(Logger.LogFile)) Logger.Info("Log file created.");
                Process.Start(new ProcessStartInfo(Logger.LogFile) { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.Error("Could not open the log file: " + ex.Message); }
        }

        public void ExitApp()
        {
            if (IsExiting) return;
            IsExiting = true;
            Logger.Info("Multron Updater closed.");
            _main?.Close();
            _autoTimer.Stop();
            _toast?.Close();
            _tray?.Dispose();
            _showEvent?.Set();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Updater.Dispose();
            SelfUpdate.Dispose();
            _tray?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
