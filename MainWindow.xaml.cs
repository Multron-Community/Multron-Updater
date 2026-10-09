using System;
using System.Collections.Generic;
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
using MultronUpdater.Views;

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
        private readonly ObservableCollection<FileGroupVm> _groups = new();
        private UpdateProfile? _current;
        private bool _loading;
        private FileItem? _dragItem;
        private Point _dragStart;
        private bool _dragging;
        private FileGroupVm? _dropGroup;
        private bool _trayHintShown;

        private static App AppInstance => App.Current;
        private static AppSettings Settings => App.Current.Settings;

        public MainWindow()
        {
            InitializeComponent();

            foreach (var entry in Logger.ReadRecent(500)) _logs.Add(entry);
            _logView = CollectionViewSource.GetDefaultView(_logs);
            _logView.Filter = FilterLog;
            LogList.ItemsSource = _logView;
            Logger.EntryAdded += e => Dispatcher.BeginInvoke(() => AddLog(e));
            Loaded += (_, _) => ScrollLogToEnd();

            GroupsList.ItemsSource = _groups;
            SourceInitialized += (_, _) => FileDropHelper.Enable(this, OnExplorerDrop);
            PreviewMouseMove += Window_PreviewMouseMove;
            PreviewMouseLeftButtonUp += Window_PreviewMouseLeftButtonUp;

            LoadGeneral();
            ProfileList.ItemsSource = Settings.Profiles;
            ProfileList.SelectedIndex = 0;
            HookAutoSave();

            AppInstance.ProgressChanged += OnProgress;
            AppInstance.StateChanged += RefreshState;
            _clock.Tick += (_, _) => RefreshState();
            _clock.Start();
            RefreshState();
        }

        private void HookAutoSave()
        {
            foreach (var box in new[] { NameBox, OwnerBox, RepoBox, BranchBox, RepoPathBox, FolderBox, ExeBox, IntervalBox })
                box.TextChanged += (_, _) => ScheduleAutoSave();
            TokenBox.PasswordChanged += (_, _) => ScheduleAutoSave();
            foreach (var check in new[] { EnabledCheck, RestartCheck, EdgeCheck, BackupCheck, OnlyFilesCheck, NotifyCheck, MinimizedCheck, StartupCheck, SelfUpdateCheck })
                check.Click += (_, _) => ScheduleAutoSave();
            foreach (var radio in new[] { WindowsAppRadio, ConsoleAppRadio, ConsoleShowRadio, ConsoleKeepRadio, ConsoleHiddenRadio,
                                          SourceBothRadio, SourceRepoRadio, SourceReleasesRadio })
                radio.Click += (_, _) => ScheduleAutoSave();
            ExtractZipCheck.Click += (_, _) => ScheduleAutoSave();
            ArgsBox.TextChanged += (_, _) => ScheduleAutoSave();
            AssetBox.TextChanged += (_, _) => { ScheduleAutoSave(); UpdateSourceUi(); };
            ExeBox.TextChanged += (_, _) => UpdateSourceUi();
            RepoPathBox.TextChanged += (_, _) => UpdateSourceUi();
            ExeBox.TextChanged += (_, _) => UpdateDetectedType();
            FolderBox.TextChanged += (_, _) => UpdateDetectedType();

            _autoSave.Tick += (_, _) => { _autoSave.Stop(); SaveForm(showConfirmation: false, silent: true); };
            _savedFade.Tick += (_, _) => { _savedFade.Stop(); SavedText.Text = ""; };
        }

        private void ScheduleAutoSave()
        {
            if (_loading) return;
            _autoSave.Stop();
            _autoSave.Start();
        }

        private void FlushAutoSave()
        {
            if (!_autoSave.IsEnabled) return;
            _autoSave.Stop();
            SaveForm(showConfirmation: false, silent: true);
        }

        private void LoadGeneral()
        {
            _loading = true;
            try
            {
                var s = Settings;
                AutoToggle.IsChecked = s.AutoUpdateEnabled;
                IntervalBox.Text = s.CheckIntervalMinutes.ToString();
                NotifyCheck.IsChecked = s.NotifyOnUpdate;
                MinimizedCheck.IsChecked = s.StartMinimized;
                SelfUpdateCheck.IsChecked = s.SelfUpdateEnabled;
                try { StartupCheck.IsChecked = StartupHelper.IsEnabled(); }
                catch { StartupCheck.IsChecked = s.StartWithWindows; }
            }
            finally { _loading = false; }
        }

        private void LoadProfile(UpdateProfile? p)
        {
            _loading = true;
            try
            {
                Editor.IsEnabled = p != null;
                p ??= new UpdateProfile { Name = "" };
                NameBox.Text = p.Name;
                EnabledCheck.IsChecked = p.Enabled;
                OwnerBox.Text = p.Owner;
                RepoBox.Text = p.Repo;
                BranchBox.Text = p.Branch;
                RepoPathBox.Text = p.RepoPath;
                TokenBox.Password = p.GetToken();
                FolderBox.Text = p.LocalFolder;
                ExeBox.Text = p.ExeName;
                WindowsAppRadio.IsChecked = !p.IsConsoleApp;
                ConsoleAppRadio.IsChecked = p.IsConsoleApp;
                ArgsBox.Text = p.StartArguments;
                ConsoleHiddenRadio.IsChecked = p.HideConsole;
                ConsoleKeepRadio.IsChecked = !p.HideConsole && p.KeepConsoleOpen;
                ConsoleShowRadio.IsChecked = !p.HideConsole && !p.KeepConsoleOpen;
                ConsoleOptions.Visibility = p.IsConsoleApp ? Visibility.Visible : Visibility.Collapsed;
                UpdateDetectedType();
                SourceBothRadio.IsChecked = p.Source == TargetSource.Both;
                SourceRepoRadio.IsChecked = p.Source == TargetSource.Repository;
                SourceReleasesRadio.IsChecked = p.Source == TargetSource.Releases;
                AssetBox.Text = p.ReleaseAsset;
                ExtractZipCheck.IsChecked = p.ExtractZipAssets;
                ReleaseInfoText.Text = p.InstalledReleaseFiles.Count > 0 ? $"Installed release: {p.InstalledReleaseTag}" : "";
                UpdateSourceUi();
                RestartCheck.IsChecked = p.RestartAfterUpdate;
                EdgeCheck.IsChecked = p.RefreshEdgeAfterUpdate;
                BackupCheck.IsChecked = p.KeepBackup;
                OnlyFilesCheck.IsChecked = p.OnlySelectedFiles;
                _groups.Clear();
                foreach (var g in p.Groups) AddGroupVm(new FileGroupVm(g));
                FileFilterBox.Text = "";
                UpdateFilesSummary(null);
                UrlBox.Text = !string.IsNullOrWhiteSpace(p.Owner) && !string.IsNullOrWhiteSpace(p.Repo)
                    ? $"https://github.com/{p.Owner}/{p.Repo}/tree/{p.Branch}/{p.RepoPath}".TrimEnd('/')
                    : "";
            }
            finally { _loading = false; }
        }

        private void ReadProfile(UpdateProfile p)
        {
            p.Name = NameBox.Text.Trim();
            p.Enabled = EnabledCheck.IsChecked == true;
            p.Owner = OwnerBox.Text.Trim();
            p.Repo = RepoBox.Text.Trim();
            if (p.Repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) p.Repo = p.Repo[..^4];
            p.Branch = string.IsNullOrWhiteSpace(BranchBox.Text) ? "main" : BranchBox.Text.Trim();
            p.RepoPath = RepoPathBox.Text.Trim().Replace('\\', '/').Trim('/');
            p.SetToken(TokenBox.Password);
            p.LocalFolder = FolderBox.Text.Trim();
            p.ExeName = ExeBox.Text.Trim();
            p.Source = SourceReleasesRadio.IsChecked == true ? TargetSource.Releases
                     : SourceRepoRadio.IsChecked == true ? TargetSource.Repository
                     : TargetSource.Both;
            p.ReleaseAsset = AssetBox.Text.Trim();
            p.ExtractZipAssets = ExtractZipCheck.IsChecked == true;
            p.IsConsoleApp = ConsoleAppRadio.IsChecked == true;
            p.StartArguments = ArgsBox.Text.Trim();
            p.HideConsole = ConsoleHiddenRadio.IsChecked == true;
            p.KeepConsoleOpen = ConsoleKeepRadio.IsChecked == true;
            p.RestartAfterUpdate = RestartCheck.IsChecked == true;
            p.RefreshEdgeAfterUpdate = EdgeCheck.IsChecked == true;
            p.KeepBackup = BackupCheck.IsChecked == true;
            p.OnlySelectedFiles = OnlyFilesCheck.IsChecked == true;
            p.Groups = _groups.Select(g => g.ToModel()).ToList();
            p.NotifyDisplayChanged();
        }

        private bool ReadGeneral(AppSettings s, bool silent)
        {
            if (!int.TryParse(IntervalBox.Text.Trim(), out var minutes) || minutes < 1 || minutes > 1440)
            {
                if (silent)
                    minutes = s.CheckIntervalMinutes;
                else
                {
                    Tabs.SelectedIndex = 1;
                    ShowWarning("The check interval must be a whole number between 1 and 1440 minutes.");
                    IntervalBox.Focus();
                    return false;
                }
            }
            s.CheckIntervalMinutes = minutes;
            s.AutoUpdateEnabled = AutoToggle.IsChecked == true;
            s.NotifyOnUpdate = NotifyCheck.IsChecked == true;
            s.StartMinimized = MinimizedCheck.IsChecked == true;
            s.SelfUpdateEnabled = SelfUpdateCheck.IsChecked == true;
            s.StartWithWindows = StartupCheck.IsChecked == true;
            return true;
        }

        private static string TimerSignature(AppSettings s) =>
            $"{s.AutoUpdateEnabled}|{s.CheckIntervalMinutes}|" +
            string.Join(";", s.Profiles.Where(p => p.Enabled && p.IsConfigured(out _)).Select(p => p.SourceKey));

        private bool SaveForm(bool showConfirmation, bool silent = false)
        {
            var s = Settings;
            var before = TimerSignature(s);

            if (!ReadGeneral(s, silent)) return false;
            if (_current != null) ReadProfile(_current);

            if (!silent && _current != null && !string.IsNullOrEmpty(_current.ExeName) && !string.IsNullOrEmpty(_current.LocalFolder) &&
                !File.Exists(Path.Combine(_current.LocalFolder, _current.ExeName)))
                Logger.Warn($"[{_current.DisplayName}] {_current.ExeName} does not exist in the target folder yet; it will be downloaded on the first update.");

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

            var after = TimerSignature(s);
            if (!silent) Logger.Info("Settings saved.");
            if (!silent || before != after)
                AppInstance.ApplyAutoUpdate(checkSoon: s.AutoUpdateEnabled && before != after);

            SavedText.Text = "✓ Saved";
            _savedFade.Stop();
            _savedFade.Start();

            if (showConfirmation)
            {
                if (_current == null || _current.IsConfigured(out var err)) SetStatus("Settings saved", null);
                else SetStatus($"Settings saved, but \"{_current.DisplayName}\" is incomplete", err);
            }
            return true;
        }

        private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ProfileList.SelectedItem as UpdateProfile;
            if (ReferenceEquals(selected, _current)) return;
            FlushAutoSave();
            _current = selected;
            LoadProfile(_current);
            RefreshState();
        }

        private void SelectProfile(UpdateProfile p)
        {
            ProfileList.Items.Refresh();
            ProfileList.SelectedItem = p;
            ProfileList.ScrollIntoView(p);
        }

        private void AddProfile_Click(object sender, RoutedEventArgs e)
        {
            FlushAutoSave();
            var p = new UpdateProfile { Name = $"Target {Settings.Profiles.Count + 1}" };
            Settings.Profiles.Add(p);
            AppInstance.SaveSettings();
            Logger.Info($"Target \"{p.DisplayName}\" added.");
            SelectProfile(p);
            Tabs.SelectedIndex = 0;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            FlushAutoSave();
            var copy = _current.Duplicate();
            Settings.Profiles.Insert(Settings.Profiles.IndexOf(_current) + 1, copy);
            AppInstance.SaveSettings();
            Logger.Info($"Target \"{copy.DisplayName}\" added (copy).");
            SelectProfile(copy);
        }

        private void RemoveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (Settings.Profiles.Count <= 1)
            {
                ShowWarning("At least one target is needed. Change its settings or turn off \"Enabled\" instead.");
                return;
            }
            if (MessageBox.Show(this, $"Remove the target \"{_current.DisplayName}\"?\n\nFiles on this computer are not deleted.",
                    "Remove target", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _autoSave.Stop();
            var index = Settings.Profiles.IndexOf(_current);
            var removed = _current;
            Settings.Profiles.Remove(removed);
            _current = null;
            AppInstance.SaveSettings();
            AppInstance.ApplyAutoUpdate(checkSoon: false);
            Logger.Info($"Target \"{removed.DisplayName}\" removed.");
            SelectProfile(Settings.Profiles[Math.Min(index, Settings.Profiles.Count - 1)]);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            SaveForm(showConfirmation: true);
        }

        private async void CheckAllButton_Click(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            if (!SaveForm(showConfirmation: false)) return;
            if (!Settings.HasConfiguredProfile())
            {
                ShowWarning("There is no enabled, fully configured target yet.");
                return;
            }
            await AppInstance.RunCheckAsync(null, force: false, manual: true);
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            if (_current == null || !SaveForm(showConfirmation: false)) return;
            if (!_current.IsConfigured(out var err)) { ShowWarning(err); return; }
            await AppInstance.RunCheckAsync(_current.Id, force: false, manual: true);
        }

        private async void ForceButton_Click(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            if (_current == null || !SaveForm(showConfirmation: false)) return;
            if (!_current.IsConfigured(out var err)) { ShowWarning(err); return; }

            var steps = new List<string> { "all files will be downloaded again from GitHub" };
            if (!string.IsNullOrEmpty(_current.ExeName)) steps.Add($"{_current.ExeName} will be closed");
            steps.Add("the files will be replaced");
            if (!string.IsNullOrEmpty(_current.ExeName) && _current.RestartAfterUpdate) steps.Add($"{_current.ExeName} will be started again");
            if (_current.RefreshEdgeAfterUpdate) steps.Add("the active Edge tab will be refreshed");

            if (MessageBox.Show(this, $"Force update \"{_current.DisplayName}\":\n\n• " + string.Join("\n• ", steps) + "\n\nContinue?",
                    "Force update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await AppInstance.RunCheckAsync(_current.Id, force: true, manual: true);
        }

        private void AutoToggle_Click(object sender, RoutedEventArgs e)
        {
            bool on = AutoToggle.IsChecked == true;
            if (on)
            {
                FlushAutoSave();
                if (_current != null) ReadProfile(_current);
                if (!Settings.HasConfiguredProfile())
                {
                    AutoToggle.IsChecked = false;
                    Tabs.SelectedIndex = 0;
                    ShowWarning("Fill in at least one target (GitHub source and target folder) before turning on automatic updates.");
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
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text.StartsWith("Target ") || NameBox.Text == "My program")
                NameBox.Text = repo;
            SetStatus("Fields filled from the link", "Check them; changes are saved automatically.");
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
                Title = "Select the program to close while updating",
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

            if (ProgramRunner.IsConsoleExe(file) is bool console)
            {
                WindowsAppRadio.IsChecked = !console;
                ConsoleAppRadio.IsChecked = console;
                ScheduleAutoSave();
            }
        }

        private void Source_Changed(object sender, RoutedEventArgs e) => UpdateSourceUi();

        private void UpdateSourceUi()
        {
            if (ReleaseOptions == null || SourceRepoRadio == null) return;
            bool releases = SourceRepoRadio.IsChecked != true;
            ReleaseOptions.Visibility = releases ? Visibility.Visible : Visibility.Collapsed;

            var probe = new UpdateProfile { ReleaseAsset = AssetBox.Text, ExeName = ExeBox.Text, RepoPath = RepoPathBox.Text };
            var effective = probe.EffectiveAssetPattern();
            AssetHint.Text = string.IsNullOrWhiteSpace(AssetBox.Text)
                ? (effective.Length > 0
                    ? $"Empty: the release file named '{effective}' is used (from the program / repository path). Wildcards work: MyApp-*.zip"
                    : "Enter the file name of the release asset, e.g. MyApp.exe or MyApp-*-win64.zip (several: a.exe;b.dll).")
                : "Wildcards (*, ?) and several names separated by ';' are allowed.";
        }

        private async void ShowRelease_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _autoSave.Stop();
            if (!SaveForm(showConfirmation: false)) return;
            CheckReleaseButton.IsEnabled = false;
            ReleaseInfoText.Text = "Loading the latest release...";
            var profile = _current.Clone();
            try
            {
                var release = await System.Threading.Tasks.Task.Run(() => AppInstance.Updater.GetLatestReleaseAsync(profile));
                if (release == null)
                {
                    ReleaseInfoText.Text = $"{profile.Owner}/{profile.Repo} has no published release yet.";
                    return;
                }
                var pattern = profile.EffectiveAssetPattern();
                var lines = release.Assets.Select(a =>
                    $"{(pattern.Length > 0 && UpdateService.MatchesPattern(a.Name, pattern) ? "✓" : "  ")} {a.Name}  ({UpdateService.FormatSize(a.Size)}{(a.Sha256 != null ? ", SHA-256" : "")})");
                ReleaseInfoText.Text = $"Latest release: {release.Tag}{(release.Name.Length > 0 && release.Name != release.Tag ? " – " + release.Name : "")}" +
                                       (profile.InstalledReleaseFiles.Count > 0 ? $"   ·   installed: {profile.InstalledReleaseTag}" : "") +
                                       Environment.NewLine + (release.Assets.Count == 0 ? "This release has no files." : string.Join(Environment.NewLine, lines)) +
                                       (pattern.Length > 0 && !release.Assets.Any(a => UpdateService.MatchesPattern(a.Name, pattern))
                                           ? Environment.NewLine + $"No file matches '{pattern}'." : "");
            }
            catch (Exception ex)
            {
                ReleaseInfoText.Text = "Could not load the release: " + ex.Message;
            }
            finally { CheckReleaseButton.IsEnabled = true; }
        }

        private void ProgramType_Changed(object sender, RoutedEventArgs e)
        {
            if (ConsoleOptions == null) return;
            ConsoleOptions.Visibility = ConsoleAppRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDetectedType()
        {
            if (DetectedTypeText == null) return;
            string? path = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(FolderBox.Text) && !string.IsNullOrWhiteSpace(ExeBox.Text))
                    path = Path.Combine(FolderBox.Text.Trim(), ExeBox.Text.Trim());
            }
            catch { }
            var detected = path != null && File.Exists(path) ? ProgramRunner.IsConsoleExe(path) : null;
            DetectedTypeText.Text = detected switch
            {
                true => "(the selected .exe is a console app)",
                false => "(the selected .exe is a Windows app)",
                _ => ""
            };
        }

        private void StartNow_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _autoSave.Stop();
            if (!SaveForm(showConfirmation: false)) return;
            var exe = ProgramRunner.ExePath(_current);
            if (exe == null) { ShowWarning("Choose the target folder and the program (.exe) first."); return; }
            if (!File.Exists(exe)) { ShowWarning($"The program does not exist yet:\n{exe}"); return; }
            ProgramRunner.Start(_current, exe);
            SetStatus($"Started {Path.GetFileName(exe)}", null);
        }

        private void OpenConsoleLog_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var file = ProgramRunner.ConsoleLogFile(_current);
            if (File.Exists(file)) OpenPath(file);
            else ShowWarning("There is no console log yet. It is created when the program runs hidden.");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(FolderBox.Text)) OpenPath(FolderBox.Text);
            else ShowWarning("The target folder does not exist yet.");
        }

        private void AddGroupVm(FileGroupVm vm)
        {
            vm.Changed += () => { ScheduleAutoSave(); UpdateFilesSummary(null); };
            _groups.Add(vm);
        }

        private FileGroupVm CreateGroup(string name)
        {
            var vm = new FileGroupVm(new FileGroup { Name = name, RestartProgram = !string.IsNullOrWhiteSpace(ExeBox.Text) });
            AddGroupVm(vm);
            ScheduleAutoSave();
            return vm;
        }

        private IEnumerable<FileItem> AllFileItems() => _groups.SelectMany(g => g.Files);

        private void UpdateFilesSummary(string? extra)
        {
            int total = AllFileItems().Count();
            int on = _groups.Where(g => g.Enabled).SelectMany(g => g.Files).Count(f => f.Include);
            var text = total == 0
                ? "No files yet. Click \"Load from GitHub\", \"Add files...\" or drag files below."
                : $"{on} of {total} file(s) will be updated, in {_groups.Count} group(s).";
            FilesSummary.Text = extra == null ? text : $"{text}  {extra}";
        }

        private void NewGroup_Click(object sender, RoutedEventArgs e)
        {
            var vm = CreateGroup($"Group {_groups.Count + 1}");
            OnlyFilesCheck.IsChecked = true;
            ScheduleAutoSave();
            UpdateFilesSummary(null);
            GroupsList.UpdateLayout();
            if (GroupsList.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement fe) fe.BringIntoView();
        }

        private void RemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FileGroupVm vm) return;
            if (vm.Files.Count > 0 && MessageBox.Show(this,
                    $"Delete the group \"{vm.Name}\" and remove its {vm.Files.Count} file(s) from the list?\n\nFiles on this computer are not deleted.",
                    "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _groups.Remove(vm);
            ScheduleAutoSave();
            UpdateFilesSummary(null);
        }

        private void FileFilter_Changed(object sender, TextChangedEventArgs e)
        {
            var q = FileFilterBox.Text.Trim();
            foreach (var f in AllFileItems())
                f.IsVisible = q.Length == 0 || f.Path.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select the files to keep updated", Multiselect = true, Filter = "All files (*.*)|*.*" };
            if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
            if (dlg.ShowDialog(this) == true) AddLocalPaths(dlg.FileNames, null);
        }

        private void OnExplorerDrop(IReadOnlyList<string> paths, Point point)
        {
            if (_current == null || paths.Count == 0) return;
            if (Tabs.SelectedIndex != 0) Tabs.SelectedIndex = 0;
            AddLocalPaths(paths, GroupAt(point));
        }

        private FileGroupVm? GroupAt(Point windowPoint)
        {
            var hit = InputHitTest(windowPoint) as DependencyObject;
            while (hit != null)
            {
                if (hit is FrameworkElement { Tag: FileGroupVm g } fe && fe.Name == "groupBorder") return g;
                hit = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            }
            return null;
        }

        private void AddLocalPaths(IEnumerable<string> paths, FileGroupVm? target)
        {
            var list = paths.ToList();
            var root = FolderBox.Text.Trim();
            if (root.Length == 0)
            {
                var first = list[0];
                root = Directory.Exists(first) && list.Count == 1 ? first : Path.GetDirectoryName(first)!;
                FolderBox.Text = root;
                Logger.Info($"[{_current?.DisplayName}] Target folder set to {root} from the dropped files.");
            }
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";

            var files = new List<string>();
            foreach (var p in list)
            {
                if (File.Exists(p)) files.Add(p);
                else if (Directory.Exists(p))
                {
                    try { files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Take(5000)); }
                    catch (Exception ex) { Logger.Warn("Could not read folder " + p + ": " + ex.Message); }
                }
            }

            bool moveExisting = target != null;
            target ??= _groups.FirstOrDefault() ?? CreateGroup("Files");
            var existing = AllFileItems().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            int added = 0, rechecked = 0, moved = 0, outside = 0;
            foreach (var file in files)
            {
                var full = Path.GetFullPath(file);
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) { outside++; continue; }
                var rel = full[rootFull.Length..].Replace('\\', '/');
                if (existing.TryGetValue(rel, out var item))
                {
                    if (moveExisting && !target.Files.Contains(item))
                    {
                        _groups.FirstOrDefault(g => g.Files.Contains(item))?.Remove(item);
                        target.Add(item);
                        moved++;
                    }
                    if (!item.Include) { item.Include = true; rechecked++; }
                    continue;
                }
                var fi = new FileItem(rel, true);
                target.Add(fi);
                existing[rel] = fi;
                added++;
            }
            target.Sort();
            target.IsExpanded = true;
            OnlyFilesCheck.IsChecked = true;
            FileFilter_Changed(FileFilterBox, null!);
            ScheduleAutoSave();

            var parts = new List<string>();
            if (added > 0) parts.Add($"added {added} file(s) to \"{target.Name}\"");
            if (moved > 0) parts.Add($"moved {moved} file(s) to \"{target.Name}\"");
            if (rechecked > 0) parts.Add($"checked {rechecked} file(s) already in the list");
            if (parts.Count == 0) parts.Add("all dropped files were already in the list");
            var msg = char.ToUpper(parts[0][0]) + string.Join(", ", parts)[1..] + ".";
            if (outside > 0) msg += $" {outside} file(s) skipped because they are not inside the target folder.";
            Logger.Info($"[{_current?.DisplayName}] {msg}");
            UpdateFilesSummary(msg);
            if (outside > 0 && added == 0 && rechecked == 0 && moved == 0)
                ShowWarning($"The dropped files are not inside the target folder:\n{root}\n\nChange the target folder first, or drop files from inside it.");
        }

        private async void LoadFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _autoSave.Stop();
            if (!SaveForm(showConfirmation: false)) return;
            if (!_current.IsConfigured(out var err)) { ShowWarning(err); return; }

            LoadFilesButton.IsEnabled = false;
            UpdateFilesSummary("Loading the file list from GitHub...");
            var profile = _current;
            try
            {
                var snapshot = profile.Clone();
                var statuses = await System.Threading.Tasks.Task.Run(() => AppInstance.Updater.GetFileStatusAsync(snapshot));
                if (!ReferenceEquals(profile, _current)) return;

                var existing = AllFileItems().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                bool wasEmpty = existing.Count == 0;
                FileGroupVm? newGroup = wasEmpty ? (_groups.FirstOrDefault() ?? CreateGroup("Files")) : null;
                int added = 0;
                foreach (var st in statuses)
                {
                    if (existing.TryGetValue(st.RelativePath, out var item)) { item.State = st.State; continue; }
                    if (st.State == FileState.NotOnGitHub) continue;
                    newGroup ??= _groups.FirstOrDefault(g => g.Name == "Other files on GitHub") ?? CreateGroup("Other files on GitHub");
                    var fi = new FileItem(st.RelativePath, wasEmpty, st.State);
                    newGroup.Add(fi);
                    existing[fi.Path] = fi;
                    added++;
                }
                foreach (var g in _groups) g.Sort();
                if (added > 0) OnlyFilesCheck.IsChecked = true;
                FileFilter_Changed(FileFilterBox, null!);
                ScheduleAutoSave();

                int changed = statuses.Count(s => s.State == FileState.Changed);
                int fresh = statuses.Count(s => s.State == FileState.New);
                int same = statuses.Count(s => s.State == FileState.UpToDate);
                int missing = statuses.Count(s => s.State == FileState.NotOnGitHub);
                var summary = $"GitHub: {changed} changed, {fresh} new, {same} up to date" + (missing > 0 ? $", {missing} not found" : "") +
                              (added > 0 ? $" · {added} file(s) added to the list" + (wasEmpty ? "" : " (unchecked, in \"Other files on GitHub\")") : "") + ".";
                Logger.Info($"[{profile.DisplayName}] {summary}");
                UpdateFilesSummary(summary);
            }
            catch (Exception ex)
            {
                Logger.Error($"[{profile.DisplayName}] Could not load the file list: {ex.Message}");
                UpdateFilesSummary("Could not load the file list: " + ex.Message);
            }
            finally { LoadFilesButton.IsEnabled = true; }
        }

        private void FileRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FileItem item) return;
            _dragItem = item;
            _dragStart = e.GetPosition(this);
            _dragging = false;
        }

        private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_dragItem == null) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { EndDrag(); return; }

            var pos = e.GetPosition(this);
            if (!_dragging)
            {
                if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6) return;
                _dragging = true;
                System.Windows.Input.Mouse.Capture(this);
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Hand;
                SetStatus($"Moving \"{_dragItem.Name}\"", "Release it over the group it should belong to.");
            }

            var group = GroupAt(pos);
            if (!ReferenceEquals(group, _dropGroup))
            {
                if (_dropGroup != null) _dropGroup.IsDropTarget = false;
                _dropGroup = group;
                if (_dropGroup != null) _dropGroup.IsDropTarget = true;
            }
        }

        private void Window_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_dragItem == null) return;
            if (_dragging)
            {
                var target = GroupAt(e.GetPosition(this));
                if (target != null) MoveFileToGroup(_dragItem, target);
                e.Handled = true;
            }
            EndDrag();
        }

        private void EndDrag()
        {
            if (_dragging)
            {
                System.Windows.Input.Mouse.Capture(null);
                System.Windows.Input.Mouse.OverrideCursor = null;
                SetStatus("Ready", null);
            }
            if (_dropGroup != null) _dropGroup.IsDropTarget = false;
            _dropGroup = null;
            _dragItem = null;
            _dragging = false;
        }

        private void MoveFileToGroup(FileItem item, FileGroupVm target)
        {
            var source = _groups.FirstOrDefault(g => g.Files.Contains(item));
            if (source == null || ReferenceEquals(source, target)) return;
            source.Remove(item);
            target.Add(item);
            target.Sort();
            target.IsExpanded = true;
            ScheduleAutoSave();
            UpdateFilesSummary($"Moved {item.Name} to \"{target.Name}\".");
        }

        private void FileRow_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not FileItem item) return;
            var source = _groups.FirstOrDefault(g => g.Files.Contains(item));
            var menu = new ContextMenu();

            var move = new MenuItem { Header = "Move to group" };
            foreach (var g in _groups.Where(g => !ReferenceEquals(g, source)))
            {
                var target = g;
                var mi = new MenuItem { Header = g.Name };
                mi.Click += (_, _) => MoveFileToGroup(item, target);
                move.Items.Add(mi);
            }
            if (move.Items.Count > 0) move.Items.Add(new Separator());
            var newGroup = new MenuItem { Header = "New group" };
            newGroup.Click += (_, _) => MoveFileToGroup(item, CreateGroup($"Group {_groups.Count + 1}"));
            move.Items.Add(newGroup);
            menu.Items.Add(move);

            var openFolder = new MenuItem { Header = "Show in File Explorer" };
            openFolder.Click += (_, _) =>
            {
                var full = Path.Combine(FolderBox.Text, item.Path.Replace('/', '\\'));
                if (File.Exists(full)) Process.Start("explorer.exe", $"/select,\"{full}\"");
                else ShowWarning("This file is not on this computer yet.");
            };
            menu.Items.Add(openFolder);

            var remove = new MenuItem { Header = "Remove from list" };
            remove.Click += (_, _) =>
            {
                source?.Remove(item);
                ScheduleAutoSave();
                UpdateFilesSummary(null);
            };
            menu.Items.Add(remove);

            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void OnProgress(UpdateProgress p)
        {
            if (p.Stage == UpdateStage.Downloading && p.BytesTotal > 0)
            {
                var pct = p.BytesDone * 100 / p.BytesTotal;
                SetStatus($"{p.ProfileName}: downloading update... {pct}%",
                    $"{p.CurrentFile}  ·  {UpdateService.FormatSize(p.BytesDone)} of {UpdateService.FormatSize(p.BytesTotal)}");
                Taskbar.ProgressState = TaskbarItemProgressState.Normal;
                Taskbar.ProgressValue = pct / 100.0;
            }
            else
            {
                SetStatus($"{p.ProfileName}: {p.Message}", p.Stage == UpdateStage.Failed ? "See the Logs tab for details." : null);
                Taskbar.ProgressState = p.Stage is UpdateStage.Checking or UpdateStage.Failed or UpdateStage.Completed
                    ? TaskbarItemProgressState.None
                    : TaskbarItemProgressState.Indeterminate;
            }
            if (p.Stage == UpdateStage.Failed)
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E));
        }

        private void SetStatus(string text, string? detail)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource("TextBrush");
            StatusDetail.Text = detail ?? BuildDetail();
        }

        private string BuildDetail()
        {
            var parts = new List<string>();
            var enabled = Settings.Profiles.Count(p => p.Enabled);
            parts.Add($"{enabled} of {Settings.Profiles.Count} target(s) enabled");
            if (AppInstance.LastCheck is { } lc) parts.Add($"Last check {lc:HH:mm:ss}");
            if (_current?.LastCommitSha is { Length: > 0 } sha) parts.Add($"{_current.DisplayName}: commit {sha[..Math.Min(7, sha.Length)]}");
            if (_current?.LastUpdateTime is { } lu) parts.Add($"updated {lu:yyyy-MM-dd HH:mm}");
            return string.Join("  ·  ", parts);
        }

        private void RefreshState()
        {
            var s = Settings;
            bool on = s.AutoUpdateEnabled;
            AutoToggle.IsChecked = on;
            AutoBadgeText.Text = on ? "Auto update: ON" : "Auto update: OFF";
            AutoDot.Fill = on ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
            AutoBadge.Background = on ? new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x26)) : new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));

            NextCheckText.Text = on
                ? (AppInstance.NextCheck is { } nc ? $"Next check at {nc:HH:mm}." : "Automatic updates are on, but no target is fully configured.")
                : "Automatic updates are off. Use \"Check all now\" or \"Check this target\" to update manually.";

            bool busy = AppInstance.IsChecking;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CheckAllButton.IsEnabled = CheckButton.IsEnabled = ForceButton.IsEnabled = !busy;
            if (!busy) StatusDetail.Text = BuildDetail();

            VersionText.Text = $"Version {SelfUpdater.CurrentVersion}";
            var selfStatus = AppInstance.SelfUpdateStatus;
            if (string.IsNullOrEmpty(selfStatus))
                selfStatus = s.LastSelfUpdateCheck is { } lsc ? $"Last checked {lsc:yyyy-MM-dd HH:mm}." : "Not checked yet.";
            SelfStatusText.Text = selfStatus;
            SelfInstallButton.Visibility = AppInstance.AvailableUpdate != null && SelfUpdater.CanUpdateItself(out _)
                ? Visibility.Visible : Visibility.Collapsed;
            SelfInstallButton.Content = AppInstance.AvailableUpdate != null ? $"Install {AppInstance.AvailableUpdate.Version}" : "Install update";
            SelfInstallButton.IsEnabled = SelfCheckButton.IsEnabled = !AppInstance.IsSelfUpdating;
        }

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

        private async void SelfCheck_Click(object sender, RoutedEventArgs e) => await AppInstance.CheckSelfUpdateAsync(manual: true);

        private async void SelfInstall_Click(object sender, RoutedEventArgs e)
        {
            var release = AppInstance.AvailableUpdate;
            if (release == null) return;
            if (MessageBox.Show(this, $"Update Multron Updater from {SelfUpdater.CurrentVersion} to {release.Version}?\n\nIt will restart after the update.",
                    "Update Multron Updater", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            FlushAutoSave();
            await AppInstance.InstallSelfUpdateAsync();
        }

        private void SelfReleases_Click(object sender, RoutedEventArgs e) =>
            OpenPath($"https://github.com/{SelfUpdater.Owner}/{SelfUpdater.Repo}/releases");

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            OpenPath(AppSettings.DataFolder);
        }

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
