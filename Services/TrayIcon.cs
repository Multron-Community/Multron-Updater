using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace MultronUpdater.Services
{
    public sealed class TrayIcon : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly Icon _baseIcon;
        private readonly List<Icon> _frames = new();
        private readonly WinForms.Timer _animTimer = new() { Interval = 90 };
        private readonly WinForms.ToolStripMenuItem _autoItem;
        private int _frame;

        public event Action? OpenRequested;
        public event Action? CheckRequested;
        public event Action<bool>? AutoUpdateToggled;
        public event Action? OpenLogRequested;
        public event Action? ExitRequested;

        public TrayIcon(Icon appIcon, bool autoUpdate)
        {
            _baseIcon = new Icon(appIcon, WinForms.SystemInformation.SmallIconSize);
            BuildFrames(appIcon);

            var menu = new WinForms.ContextMenuStrip();
            var open = new WinForms.ToolStripMenuItem("Open Multron Updater", null, (_, _) => OpenRequested?.Invoke());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("Check for updates now", null, (_, _) => CheckRequested?.Invoke());
            _autoItem = new WinForms.ToolStripMenuItem("Automatic updates") { Checked = autoUpdate, CheckOnClick = true };
            _autoItem.CheckedChanged += (_, _) => AutoUpdateToggled?.Invoke(_autoItem.Checked);
            menu.Items.Add(_autoItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Open log file", null, (_, _) => OpenLogRequested?.Invoke());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

            _icon = new WinForms.NotifyIcon
            {
                Icon = _baseIcon,
                Text = "Multron Updater",
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
            _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();

            _animTimer.Tick += (_, _) =>
            {
                _frame = (_frame + 1) % _frames.Count;
                _icon.Icon = _frames[_frame];
            };
        }

        public void SetAutoUpdate(bool enabled)
        {
            if (_autoItem.Checked != enabled) _autoItem.Checked = enabled;
        }

        public void SetTooltip(string text)
        {
            text = "Multron Updater - " + text;
            _icon.Text = text.Length > 127 ? text[..127] : text;
        }

        public void StartAnimation()
        {
            if (!_animTimer.Enabled) { _frame = 0; _animTimer.Start(); }
        }

        public void StopAnimation()
        {
            _animTimer.Stop();
            _icon.Icon = _baseIcon;
        }

        public void ShowBalloon(string title, string text, bool error = false) =>
            _icon.ShowBalloonTip(4000, title, text, error ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Info);

        private void BuildFrames(Icon appIcon)
        {
            var size = WinForms.SystemInformation.SmallIconSize.Width;
            using var src = new Icon(appIcon, 64, 64).ToBitmap();
            for (int i = 0; i < 12; i++)
            {
                using var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TranslateTransform(size / 2f, size / 2f);
                    g.RotateTransform(i * 30);
                    g.TranslateTransform(-size / 2f, -size / 2f);
                    g.DrawImage(src, 0, 0, size, size);
                }
                var h = bmp.GetHicon();
                _frames.Add((Icon)Icon.FromHandle(h).Clone());
                DestroyIcon(h);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _animTimer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            foreach (var f in _frames) f.Dispose();
            _baseIcon.Dispose();
        }
    }
}
