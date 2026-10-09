using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MultronUpdater.Services
{
    public static class FileDropHelper
    {
        private const int WM_DROPFILES = 0x0233;
        private const int WM_COPYDATA = 0x004A;
        private const int WM_COPYGLOBALDATA = 0x0049;
        private const uint MSGFLT_ALLOW = 1;

        public static void Enable(Window window, Action<IReadOnlyList<string>, Point> onDrop)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
            RevokeDragDrop(hwnd);
            DragAcceptFiles(hwnd, true);

            HwndSource.FromHwnd(hwnd)!.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_DROPFILES) return IntPtr.Zero;
                var files = new List<string>();
                try
                {
                    uint count = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < count; i++)
                    {
                        uint len = DragQueryFile(wParam, i, null, 0);
                        var sb = new StringBuilder((int)len + 1);
                        DragQueryFile(wParam, i, sb, (uint)sb.Capacity);
                        files.Add(sb.ToString());
                    }
                    DragQueryPoint(wParam, out var pt);
                    var source = HwndSource.FromHwnd(h);
                    var point = source?.CompositionTarget != null
                        ? source.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y))
                        : new Point(pt.X, pt.Y);
                    window.Dispatcher.BeginInvoke(() => onDrop(files, point));
                }
                finally { DragFinish(wParam); }
                handled = true;
                return IntPtr.Zero;
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, uint action, IntPtr changeInfo);
        [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
        [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint size);
        [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out POINT point);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    }
}
