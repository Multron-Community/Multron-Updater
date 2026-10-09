using System;
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
        private bool _toastSuppressed;   // user closed the popup during the current update
        private readonly DispatcherTimer _autoTimer = new();

        public static new App Current => (App)Application.Current;

        public AppSettings Settings { get; private set; } = new();
        public UpdateService Updater { get; } = new();
        public bool IsExiting { get; private set; }
        public DateTime? LastCheck { get; private set; }
        public DateTime? NextCheck { get; private set; }

        /// <summary>Update progress, raised on the UI thread.</summary>
        public event Action<UpdateProgress>? ProgressChanged;
        /// <summary>Raised when a check finishes or auto-update settings change.</summary>
        public event Action? StateChanged;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Single instance: a second launch just brings the existing window to front
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
            Logger.Info("Multron Updater started.");

            System.Drawing.Icon appIcon;
            using (var s = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream)
                appIcon = new System.Drawing.Icon(s);

            _tray = new TrayIcon(appIcon, Settings.AutoUpdateEnabled);
            _tray.OpenRequested += ShowMainWindow;
            _tray.CheckRequested += () => _ = RunCheckAsync(force: false, manual: true);
            _tray.AutoUpdateToggled += on => SetAutoUpdate(on);
            _tray.OpenLogRequested += OpenLogFile;
            _tray.ExitRequested += ExitApp;

            Updater.Progress += p => Dispatcher.BeginInvoke(() => OnProgress(p));

            _main = new MainWindow();
            bool launchedHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)
                                  && Settings.StartMinimized && Settings.IsConfigured(out _);
            if (!launchedHidden) ShowMainWindow();

            _autoTimer.Tick += async (_, _) =>
            {
                NextCheck = DateTime.Now + _autoTimer.Interval;
                await RunCheckAsync(force: false, manual: false);
            };
            ApplyAutoUpdate(checkSoon: true);
        }

        // ------------------------------------------------------------------ automatic updates

        /// <summary>Starts/stops the background timer according to the settings.</summary>
        public void ApplyAutoUpdate(bool checkSoon)
        {
            _autoTimer.Stop();
            _tray?.SetAutoUpdate(Settings.AutoUpdateEnabled);

            if (Settings.AutoUpdateEnabled && Settings.IsConfigured(out _))
            {
                var minutes = Math.Clamp(Settings.CheckIntervalMinutes, 1, 1440);
                _autoTimer.Interval = TimeSpan.FromMinutes(minutes);
                _autoTimer.Start();
                NextCheck = DateTime.Now + _autoTimer.Interval;
                _tray?.SetTooltip($"Automatic updates on (every {minutes} min)");

                if (checkSoon)
                {
                    // First check shortly after enabling / starting
                    var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    once.Tick += async (_, _) => { once.Stop(); await RunCheckAsync(force: false, manual: false); };
                    once.Start();
                }
            }
            else
            {
                NextCheck = null;
                _tray?.SetTooltip(Settings.AutoUpdateEnabled ? "Settings incomplete" : "Automatic updates off");
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

        /// <summary>Runs one check (and update if needed) on a background thread.</summary>
        public async Task<UpdateResult> RunCheckAsync(bool force, bool manual)
        {
            if (Updater.IsBusy)
            {
                if (manual) Logger.Warn("An update check is already running.");
                return UpdateResult.Busy;
            }
            if (!Settings.IsConfigured(out var err))
            {
                if (manual) Logger.Warn("Cannot check: " + err);
                return UpdateResult.Failed;
            }
            if (manual) Logger.Info(force ? "Force update started by user." : "Update check started by user.");

            var snapshot = Settings.Clone();
            var result = await Task.Run(() => Updater.CheckAndUpdateAsync(snapshot, force));

            LastCheck = DateTime.Now;
            if (snapshot.LastCommitSha != null) Settings.LastCommitSha = snapshot.LastCommitSha;
            if (snapshot.LastUpdateTime != null) Settings.LastUpdateTime = snapshot.LastUpdateTime;
            SaveSettings();

            if (result == UpdateResult.Updated && Settings.NotifyOnUpdate && _toast == null)
                _tray?.ShowBalloon("Multron Updater", $"{Settings.ExeName} was updated from GitHub.");

            StateChanged?.Invoke();
            return result;
        }

        private void OnProgress(UpdateProgress p)
        {
            ProgressChanged?.Invoke(p);

            switch (p.Stage)
            {
                case UpdateStage.Downloading:
                case UpdateStage.Closing:
                case UpdateStage.Installing:
                case UpdateStage.Starting:
                    _tray?.StartAnimation();
                    _tray?.SetTooltip(p.Stage == UpdateStage.Downloading && p.BytesTotal > 0
                        ? $"Downloading {p.BytesDone * 100 / p.BytesTotal}%"
                        : p.Message);
                    if (Settings.NotifyOnUpdate && !_toastSuppressed)
                    {
                        if (_toast == null)
                        {
                            var toast = new DownloadToast();
                            toast.Closed += (_, _) => { if (!toast.IsFinished) _toastSuppressed = true; _toast = null; };
                            toast.Clicked += ShowMainWindow;
                            _toast = toast;
                            toast.Show();
                        }
                        _toast.Update(p);
                    }
                    break;

                case UpdateStage.Completed:
                    _tray?.StopAnimation();
                    _tray?.SetTooltip(p.Message);
                    _toast?.ShowCompleted(p.Message);
                    _toastSuppressed = false;
                    break;

                case UpdateStage.Failed:
                    _tray?.StopAnimation();
                    _tray?.SetTooltip("Last check failed");
                    _toast?.ShowFailed(p.Message);
                    _toastSuppressed = false;
                    break;
            }
        }

        // ------------------------------------------------------------------ window / exit

        public void ShowMainWindow()
        {
            if (_main == null || IsExiting) return;
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;   // bring to front reliably
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
            _main?.Close();      // saves any pending setting changes first
            _autoTimer.Stop();
            _toast?.Close();
            _tray?.Dispose();
            _showEvent?.Set();   // let the listener thread end
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
