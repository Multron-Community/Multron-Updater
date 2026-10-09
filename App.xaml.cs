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

            _mutex = new Mutex(true, MutexName, out bool isFirst);
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
            Logger.Info($"Multron Updater started ({Settings.Profiles.Count} target(s)).");

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

            _main = new MainWindow();
            bool launchedHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)
                                  && Settings.StartMinimized && Settings.HasConfiguredProfile();
            if (!launchedHidden) ShowMainWindow();

            _autoTimer.Tick += async (_, _) =>
            {
                NextCheck = DateTime.Now + _autoTimer.Interval;
                await RunCheckAsync(null, force: false, manual: false);
            };
            ApplyAutoUpdate(checkSoon: true);
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
            _tray?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
