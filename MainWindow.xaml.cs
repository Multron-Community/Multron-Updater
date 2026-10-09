using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using MultronUpdater.Services;

namespace MultronUpdater
{
    public partial class MainWindow : Window
    {
        private const int MaxLogEntries = 3000;

        private readonly ObservableCollection<LogEntry> _logs = new();
        private readonly ICollectionView _logView;
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private readonly DispatcherTimer _savedFade = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _loading;
        private bool _trayHintShown;

        private static App AppInstance => App.Current;

        public MainWindow()
        {
            InitializeComponent();

            // --- logs ---
            foreach (var entry in Logger.ReadRecent(500)) _logs.Add(entry);
            _logView = CollectionViewSource.GetDefaultView(_logs);
            _logView.Filter = FilterLog;
            LogList.ItemsSource = _logView;
            Logger.EntryAdded += e => Dispatcher.BeginInvoke(() => AddLog(e));
            Loaded += (_, _) => ScrollLogToEnd();

            // --- state ---
            LoadForm(AppInstance.Settings);
            HookAutoSave();
            AppInstance.ProgressChanged += OnProgress;
            AppInstance.StateChanged += RefreshState;
            _clock.Tick += (_, _) => RefreshState();
            _clock.Start();
            RefreshState();
        }

        // ================================================================ settings form

        /// <summary>Every change in the form is saved automatically (shortly after typing stops).</summary>
        private void HookAutoSave()
        {
            foreach (var box in new[] { OwnerBox, RepoBox, BranchBox, RepoPathBox, FolderBox, ExeBox, IntervalBox })
                box.TextChanged += (_, _) => ScheduleAutoSave();
            TokenBox.PasswordChanged += (_, _) => ScheduleAutoSave();
            foreach (var check in new[] { RestartCheck, BackupCheck, NotifyCheck, MinimizedCheck, StartupCheck })
                check.Click += (_, _) => ScheduleAutoSave();

            _autoSave.Tick += (_, _) => { _autoSave.Stop(); SaveForm(showConfirmation: false, silent: true); };
            _savedFade.Tick += (_, _) => { _savedFade.Stop(); SavedText.Text = ""; };
        }

        private void ScheduleAutoSave()
        {
            if (_loading) return;
            _autoSave.Stop();
            _autoSave.Start();
        }

        /// <summary>Saves immediately if an automatic save is still pending.</summary>
        private void FlushAutoSave()
        {
            if (!_autoSave.IsEnabled) return;
            _autoSave.Stop();
            SaveForm(showConfirmation: false, silent: true);
        }

        private void LoadForm(AppSettings s)
        {
            _loading = true;
            try { FillForm(s); }
            finally { _loading = false; }
        }

        private void FillForm(AppSettings s)
        {
            OwnerBox.Text = s.Owner;
            RepoBox.Text = s.Repo;
            BranchBox.Text = s.Branch;
            RepoPathBox.Text = s.RepoPath;
            TokenBox.Password = s.GetToken();
            FolderBox.Text = s.LocalFolder;
            ExeBox.Text = s.ExeName;
            RestartCheck.IsChecked = s.RestartAfterUpdate;
            BackupCheck.IsChecked = s.KeepBackup;
            AutoToggle.IsChecked = s.AutoUpdateEnabled;
            IntervalBox.Text = s.CheckIntervalMinutes.ToString();
            NotifyCheck.IsChecked = s.NotifyOnUpdate;
            MinimizedCheck.IsChecked = s.StartMinimized;
            try { StartupCheck.IsChecked = StartupHelper.IsEnabled(); }
            catch { StartupCheck.IsChecked = s.StartWithWindows; }

            if (!string.IsNullOrWhiteSpace(s.Owner) && !string.IsNullOrWhiteSpace(s.Repo))
                UrlBox.Text = $"https://github.com/{s.Owner}/{s.Repo}/tree/{s.Branch}/{s.RepoPath}".TrimEnd('/');
        }

        /// <summary>
        /// Validates the form and copies it into the settings. Returns false (and shows why) when invalid.
        /// In silent mode (auto-save) an invalid interval just keeps the previous value.
        /// </summary>
        private bool ReadForm(AppSettings s, bool silent = false)
        {
            if (!int.TryParse(IntervalBox.Text.Trim(), out var minutes) || minutes < 1 || minutes > 1440)
            {
                if (silent)
                    minutes = s.CheckIntervalMinutes;
                else
                {
                    ShowWarning("The check interval must be a whole number between 1 and 1440 minutes.");
                    IntervalBox.Focus();
                    return false;
                }
            }

            s.Owner = OwnerBox.Text.Trim();
            s.Repo = RepoBox.Text.Trim();
            if (s.Repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s.Repo = s.Repo[..^4];
            s.Branch = string.IsNullOrWhiteSpace(BranchBox.Text) ? "main" : BranchBox.Text.Trim();
            s.RepoPath = RepoPathBox.Text.Trim().Replace('\\', '/').Trim('/');
            s.SetToken(TokenBox.Password);
            s.LocalFolder = FolderBox.Text.Trim();
            s.ExeName = ExeBox.Text.Trim();
            s.RestartAfterUpdate = RestartCheck.IsChecked == true;
            s.KeepBackup = BackupCheck.IsChecked == true;
            s.CheckIntervalMinutes = minutes;
            s.NotifyOnUpdate = NotifyCheck.IsChecked == true;
            s.StartMinimized = MinimizedCheck.IsChecked == true;
            s.StartWithWindows = StartupCheck.IsChecked == true;
            return true;
        }

        private bool SaveForm(bool showConfirmation, bool silent = false)
        {
            var s = AppInstance.Settings;
            // What the background timer depends on, to restart it only when needed
            var before = (s.SourceKey, s.CheckIntervalMinutes, Configured: s.IsConfigured(out _), s.AutoUpdateEnabled);

            if (!ReadForm(s, silent)) return false;
            s.AutoUpdateEnabled = AutoToggle.IsChecked == true;

            if (!silent && !string.IsNullOrEmpty(s.ExeName) && !string.IsNullOrEmpty(s.LocalFolder) &&
                !File.Exists(Path.Combine(s.LocalFolder, s.ExeName)))
                Logger.Warn($"{s.ExeName} does not exist in the target folder yet; it will be downloaded on the first update.");

            try { s.Save(); }
            catch (Exception ex)
            {
                Logger.Error("Could not save settings: " + ex.Message);
                if (!silent) ShowWarning("Could not save settings: " + ex.Message);
                return false;
            }

            try
            {
                if (StartupHelper.IsEnabled() != s.StartWithWindows)
                {
                    StartupHelper.Set(s.StartWithWindows);
                    Logger.Info(s.StartWithWindows ? "Multron Updater will start with Windows." : "Start with Windows turned off.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                if (!silent) ShowWarning(ex.Message);
            }

            var after = (s.SourceKey, s.CheckIntervalMinutes, Configured: s.IsConfigured(out _), s.AutoUpdateEnabled);
            if (!silent) Logger.Info("Settings saved.");
            if (!silent || before != after)
                AppInstance.ApplyAutoUpdate(checkSoon: s.AutoUpdateEnabled && (!silent || before.SourceKey != after.SourceKey || !before.Configured));

            SavedText.Text = "✓ Saved";
            _savedFade.Stop();
            _savedFade.Start();

            if (showConfirmation)
            {
                if (s.IsConfigured(out var err)) SetStatus("Settings saved", null);
                else SetStatus("Settings saved, but incomplete", err);
            }
            return true;
        }

        // ================================================================ buttons

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            SaveForm(showConfirmation: true);
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            if (!SaveForm(showConfirmation: false)) return;
            if (!AppInstance.Settings.IsConfigured(out var err)) { ShowWarning(err); return; }
            await AppInstance.RunCheckAsync(force: false, manual: true);
        }

        private async void ForceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveForm(showConfirmation: false)) return;
            var s = AppInstance.Settings;
            if (!s.IsConfigured(out var err)) { ShowWarning(err); return; }

            var exe = string.IsNullOrEmpty(s.ExeName) ? "the program" : s.ExeName;
            if (MessageBox.Show(this,
                    $"All files will be downloaded again from GitHub, {exe} will be closed, the files replaced and {exe} started again.\n\nContinue?",
                    "Force update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await AppInstance.RunCheckAsync(force: true, manual: true);
        }

        private void AutoToggle_Click(object sender, RoutedEventArgs e)
        {
            bool on = AutoToggle.IsChecked == true;
            if (on)
            {
                // Turning on: save the whole form first, so the background check uses the current values
                var s = AppInstance.Settings;
                if (!ReadForm(s)) { AutoToggle.IsChecked = false; return; }
                if (!s.IsConfigured(out var err))
                {
                    AutoToggle.IsChecked = false;
                    ShowWarning("Fill in the settings before turning on automatic updates:\n" + err);
                    return;
                }
            }
            AppInstance.SetAutoUpdate(on);
        }

        private void AutoBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AutoToggle.IsChecked = !(AutoToggle.IsChecked == true);
            AutoToggle_Click(AutoToggle, new RoutedEventArgs());
        }

        private void FillFromLink_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseGitHubUrl(UrlBox.Text, out var owner, out var repo, out var branch, out var path))
            {
                ShowWarning("This doesn't look like a GitHub link.\nExample: https://github.com/user/repo/blob/main/bin/Release/App.exe");
                return;
            }
            OwnerBox.Text = owner;
            RepoBox.Text = repo;
            if (branch != null) BranchBox.Text = branch;
            if (path != null) RepoPathBox.Text = path;
            if (path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ExeBox.Text))
                ExeBox.Text = Path.GetFileName(path);
            SetStatus("Fields filled from the link", "Check them and click Save settings.");
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Select the folder where the program is installed" };
            if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
            if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
        }

        private void BrowseExe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select the program to close and restart",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
            };
            if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
            if (dlg.ShowDialog(this) != true) return;

            var folder = FolderBox.Text.Trim();
            var file = dlg.FileName;
            bool insideFolder = folder.Length > 0 && Directory.Exists(folder) &&
                                Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            if (insideFolder)
                ExeBox.Text = Path.GetRelativePath(folder, file);
            else
            {
                FolderBox.Text = Path.GetDirectoryName(file)!;
                ExeBox.Text = Path.GetFileName(file);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(FolderBox.Text)) OpenPath(FolderBox.Text);
            else ShowWarning("The target folder does not exist yet.");
        }

        // ================================================================ progress / status

        private void OnProgress(UpdateProgress p)
        {
            bool busy = p.Stage is not (UpdateStage.Completed or UpdateStage.Failed);
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CheckButton.IsEnabled = ForceButton.IsEnabled = !busy;

            if (p.Stage == UpdateStage.Downloading && p.BytesTotal > 0)
            {
                var pct = p.BytesDone * 100 / p.BytesTotal;
                SetStatus($"Downloading update... {pct}%",
                    $"{p.CurrentFile}  ·  {UpdateService.FormatSize(p.BytesDone)} of {UpdateService.FormatSize(p.BytesTotal)}");
                Taskbar.ProgressState = TaskbarItemProgressState.Normal;
                Taskbar.ProgressValue = pct / 100.0;
            }
            else
            {
                SetStatus(p.Message, p.Stage == UpdateStage.Failed ? "See the Logs tab for details." : null);
                Taskbar.ProgressState = p.Stage switch
                {
                    UpdateStage.Checking => TaskbarItemProgressState.None,
                    UpdateStage.Failed => TaskbarItemProgressState.None,
                    UpdateStage.Completed => TaskbarItemProgressState.None,
                    _ => TaskbarItemProgressState.Indeterminate
                };
            }
            StatusText.Foreground = p.Stage == UpdateStage.Failed
                ? new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E))
                : (Brush)FindResource("TextBrush");
        }

        private void SetStatus(string text, string? detail)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource("TextBrush");
            StatusDetail.Text = detail ?? BuildDetail();
        }

        private string BuildDetail()
        {
            var s = AppInstance.Settings;
            var parts = new System.Collections.Generic.List<string>();
            if (AppInstance.LastCheck is { } lc) parts.Add($"Last check {lc:HH:mm:ss}");
            if (!string.IsNullOrEmpty(s.LastCommitSha)) parts.Add($"GitHub commit {s.LastCommitSha[..Math.Min(7, s.LastCommitSha.Length)]}");
            if (s.LastUpdateTime is { } lu) parts.Add($"Last update {lu:yyyy-MM-dd HH:mm}");
            return parts.Count == 0 ? "Not checked yet" : string.Join("  ·  ", parts);
        }

        private void RefreshState()
        {
            var s = AppInstance.Settings;
            bool on = s.AutoUpdateEnabled;
            AutoToggle.IsChecked = on;
            AutoBadgeText.Text = on ? "Auto update: ON" : "Auto update: OFF";
            AutoDot.Fill = on ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
            AutoBadge.Background = on ? new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x26)) : new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));

            NextCheckText.Text = on
                ? (AppInstance.NextCheck is { } nc ? $"Next check at {nc:HH:mm}." : "Automatic updates are on, but the settings are incomplete.")
                : "Automatic updates are off. Use \"Check now\" to update manually.";

            if (!AppInstance.Updater.IsBusy) StatusDetail.Text = BuildDetail();
        }

        // ================================================================ logs

        private void AddLog(LogEntry e)
        {
            _logs.Add(e);
            while (_logs.Count > MaxLogEntries) _logs.RemoveAt(0);
            if (FilterLog(e)) ScrollLogToEnd();
        }

        private void ScrollLogToEnd()
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        }

        private bool FilterLog(object o)
        {
            if (o is not LogEntry e) return false;
            if (LevelFilter != null && LevelFilter.SelectedIndex > 0 && (int)e.Level != LevelFilter.SelectedIndex - 1) return false;
            var q = SearchBox?.Text;
            return string.IsNullOrWhiteSpace(q) || e.Message.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (_logView == null) return;
            _logView.Refresh();
            ScrollLogToEnd();
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join(Environment.NewLine, _logView.Cast<LogEntry>().Select(l => l.ToString()));
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); SetStatus("Log lines copied to the clipboard", null); }
            catch (Exception ex) { ShowWarning("Could not copy: " + ex.Message); }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e) => _logs.Clear();

        private void OpenLogFile_Click(object sender, RoutedEventArgs e) => AppInstance.OpenLogFile();

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(AppSettings.DataFolder);
            OpenPath(AppSettings.DataFolder);
        }

        // ================================================================ helpers

        /// <summary>
        /// Accepts github.com/owner/repo[/tree|blob|raw/branch/path...] and
        /// raw.githubusercontent.com/owner/repo/branch/path.
        /// </summary>
        public static bool TryParseGitHubUrl(string input, out string owner, out string repo, out string? branch, out string? path)
        {
            owner = repo = ""; branch = path = null;
            input = input.Trim();
            if (input.Length == 0) return false;
            if (!input.Contains("://")) input = "https://" + input;
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;

            var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            if (seg.Length < 2) return false;
            owner = seg[0];
            repo = seg[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? seg[1][..^4] : seg[1];

            if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                if (seg.Length >= 3) branch = seg[2];
                if (seg.Length >= 4) path = string.Join("/", seg.Skip(3));
                return true;
            }
            if (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return false;

            if (seg.Length >= 4 && seg[2] is "tree" or "blob" or "raw")
            {
                branch = seg[3];
                if (seg.Length >= 5) path = string.Join("/", seg.Skip(4));
            }
            return true;
        }

        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Error("Could not open: " + ex.Message); }
        }

        private void ShowWarning(string text) =>
            MessageBox.Show(this, text, "Multron Updater", MessageBoxButton.OK, MessageBoxImage.Warning);

        protected override void OnClosing(CancelEventArgs e)
        {
            FlushAutoSave();
            if (!AppInstance.IsExiting)
            {
                // Closing the window only hides it; the updater keeps running in the tray
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    Logger.Info("Window hidden; Multron Updater keeps running in the system tray.");
                }
            }
            base.OnClosing(e);
        }
    }
}
