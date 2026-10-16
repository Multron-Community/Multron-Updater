using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultronUpdater.Services
{
    public enum AppTheme { System, Light, Dark }

    public static class ThemeManager
    {
        private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
        {
            ["AccentBrush"] = ("#2EA043", "#2EA043"),
            ["AccentDarkBrush"] = ("#1A7F37", "#3FB950"),
            ["BgBrush"] = ("#F3F5F7", "#0D1117"),
            ["CardBrush"] = ("#FFFFFF", "#161B22"),
            ["BorderBrush"] = ("#D9DEE3", "#30363D"),
            ["TextBrush"] = ("#1F2328", "#E6EDF3"),
            ["MutedBrush"] = ("#656D76", "#8B949E"),
            ["HeaderBrush"] = ("#161B22", "#010409"),
            ["SubtleBrush"] = ("#F6F8FA", "#21262D"),
            ["HoverBrush"] = ("#F3F5F7", "#21262D"),
            ["InputBrush"] = ("#FFFFFF", "#0D1117"),
            ["PlaceholderBrush"] = ("#8C959F", "#6E7681"),
            ["TrackOffBrush"] = ("#C9D1D9", "#484F58"),
            ["ChangedBrush"] = ("#BF8700", "#D29922"),
            ["SuccessBgBrush"] = ("#DAFBE1", "#1B3A26"),
            ["SuccessFgBrush"] = ("#1A7F37", "#3FB950"),
            ["ErrorBgBrush"] = ("#FFEBE9", "#3D1214"),
            ["ErrorFgBrush"] = ("#CF222E", "#F85149"),
            ["WarnBgBrush"] = ("#FFF8C5", "#3B2E00"),
            ["WarnFgBrush"] = ("#9A6700", "#D29922"),
            ["InfoBgBrush"] = ("#DDF4FF", "#0C2D6B"),
            ["InfoFgBrush"] = ("#0969DA", "#58A6FF"),
            ["DropHighlightBrush"] = ("#F0FFF4", "#12261E"),
            ["DropStrokeBrush"] = ("#AFB8C1", "#484F58"),
            ["DropBgBrush"] = ("#FAFBFC", "#0D1117"),
            ["ScrollThumbBrush"] = ("#C1C8CF", "#484F58"),
        };

        private static AppTheme _theme = AppTheme.System;
        private static bool _listening;

        public static bool IsDark { get; private set; }
        public static AppTheme Current => _theme;

        public static void Apply(AppTheme theme)
        {
            _theme = theme;
            IsDark = theme == AppTheme.Dark || (theme == AppTheme.System && WindowsUsesDarkApps());
            var res = Application.Current.Resources;
            foreach (var (key, colors) in Palette)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? colors.Dark : colors.Light));
                brush.Freeze();
                res[key] = brush;
            }
            foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);

            if (!_listening)
            {
                _listening = true;
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
                        Application.Current.Dispatcher.BeginInvoke(() => Apply(_theme));
                };
            }
        }

        public static void ApplyTitleBar(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        }

        private static bool WindowsUsesDarkApps()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
