using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using MultronUpdater.Services;

namespace MultronUpdater.Views
{
    /// <summary>
    /// Bottom-right popup (just above the taskbar) that animates while an update downloads.
    /// Its taskbar button shows the download progress as well.
    /// </summary>
    public partial class DownloadToast : Window
    {
        private readonly DispatcherTimer _autoClose = new();
        private bool _closing;

        public event Action? Clicked;

        /// <summary>True once the update finished (success or failure).</summary>
        public bool IsFinished { get; private set; }

        public DownloadToast()
        {
            InitializeComponent();
            Loaded += (_, _) => { PlaceBottomRight(); SlideIn(); StartSpinner(); };
            SizeChanged += (_, _) => PlaceBottomRight();
            Root.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Button) Clicked?.Invoke(); };
            Root.MouseEnter += (_, _) => _autoClose.Stop();
            Root.MouseLeave += (_, _) => { if (_autoClose.Interval > TimeSpan.Zero && ResultGroup.Visibility == Visibility.Visible) _autoClose.Start(); };
            _autoClose.Tick += (_, _) => { _autoClose.Stop(); SlideOutAndClose(); };
        }

        public void Update(UpdateProgress p)
        {
            _autoClose.Stop();
            SpinnerGroup.Visibility = Visibility.Visible;
            ResultGroup.Visibility = Visibility.Collapsed;

            if (p.Stage == UpdateStage.Downloading && p.BytesTotal > 0)
            {
                double pct = Math.Clamp(p.BytesDone * 100.0 / p.BytesTotal, 0, 100);
                SetIndeterminate(false);
                AnimateBar(pct);
                TitleText.Text = "Downloading update";
                FileText.Text = string.IsNullOrEmpty(p.CurrentFile) ? p.Message : Path.GetFileName(p.CurrentFile);
                DetailText.Text = $"{UpdateService.FormatSize(p.BytesDone)} of {UpdateService.FormatSize(p.BytesTotal)}" +
                                  (p.FileCount > 1 ? $"  ·  file {p.FileIndex} of {p.FileCount}" : "");
                PercentText.Text = $"{pct:0}%";
                Taskbar.ProgressState = TaskbarItemProgressState.Normal;
                Taskbar.ProgressValue = pct / 100;
                Title = $"Downloading update {pct:0}%";
            }
            else
            {
                SetIndeterminate(true);
                TitleText.Text = p.Stage switch
                {
                    UpdateStage.Closing => "Closing program",
                    UpdateStage.Installing => "Installing update",
                    UpdateStage.Starting => "Starting program",
                    _ => "Downloading update"
                };
                FileText.Text = p.Message;
                DetailText.Text = "";
                PercentText.Text = "";
                Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
                Title = "Multron Updater - " + TitleText.Text;
            }
        }

        public void ShowCompleted(string message)
        {
            IsFinished = true;
            SetIndeterminate(false);
            AnimateBar(100);
            TitleText.Text = "Update installed";
            FileText.Text = message;
            DetailText.Text = "Click to open Multron Updater";
            PercentText.Text = "100%";
            Taskbar.ProgressState = TaskbarItemProgressState.Normal;
            Taskbar.ProgressValue = 1;
            Title = "Multron Updater - Update installed";
            ShowResult(success: true);
            ScheduleClose(TimeSpan.FromSeconds(5));
        }

        public void ShowFailed(string message)
        {
            IsFinished = true;
            SetIndeterminate(false);
            Bar.Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x36, 0x33));
            TitleText.Text = "Update failed";
            FileText.Text = message;
            FileText.TextWrapping = TextWrapping.Wrap;
            FileText.TextTrimming = TextTrimming.None;
            DetailText.Text = "See the Logs tab for details";
            PercentText.Text = "";
            Taskbar.ProgressState = TaskbarItemProgressState.Error;
            Taskbar.ProgressValue = 1;
            Title = "Multron Updater - Update failed";
            ShowResult(success: false);
            ScheduleClose(TimeSpan.FromSeconds(10));
        }

        // ------------------------------------------------------------------ animations

        private void PlaceBottomRight()
        {
            var wa = SystemParameters.WorkArea;   // screen area above the taskbar
            Left = wa.Right - ActualWidth;
            Top = wa.Bottom - ActualHeight;
        }

        private void SlideIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(420, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }

        private void SlideOutAndClose()
        {
            if (_closing) return;
            _closing = true;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slide = new DoubleAnimation(0, 420, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
            slide.Completed += (_, _) => Close();
            Slide.BeginAnimation(TranslateTransform.XProperty, slide);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)));
        }

        private void StartSpinner()
        {
            Spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            ArrowBob.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-2, 2, TimeSpan.FromSeconds(0.5))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private void AnimateBar(double value)
        {
            Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(value, TimeSpan.FromMilliseconds(200)));
        }

        private void SetIndeterminate(bool on)
        {
            if (Bar.IsIndeterminate == on) return;
            Bar.IsIndeterminate = on;
            if (on)
                Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.6))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else
                Bar.BeginAnimation(OpacityProperty, null);
        }

        private void ShowResult(bool success)
        {
            SpinnerGroup.Visibility = Visibility.Collapsed;
            ResultGroup.Visibility = Visibility.Visible;
            ResultCircle.Fill = new SolidColorBrush(success ? Color.FromRgb(0x2E, 0xA0, 0x43) : Color.FromRgb(0xDA, 0x36, 0x33));
            ResultGlyph.Text = success ? "" : "";
            var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
            ResultScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ResultScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        private void ScheduleClose(TimeSpan after)
        {
            _autoClose.Interval = after;
            if (!Root.IsMouseOver) _autoClose.Start();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            SlideOutAndClose();
        }
    }
}
