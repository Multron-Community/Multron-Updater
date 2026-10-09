using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MultronUpdater.Services
{
    public static class EdgeRefresher
    {
        public static bool RefreshActiveTab(out string message)
        {
            var edgePids = new HashSet<uint>(Process.GetProcessesByName("msedge").Select(p => (uint)p.Id));
            if (edgePids.Count == 0) { message = "Microsoft Edge is not running."; return false; }

            var window = FindTopEdgeWindow(edgePids);
            if (window == IntPtr.Zero) { message = "No open Microsoft Edge window was found."; return false; }

            var previous = GetForegroundWindow();
            if (!BringToFront(window)) { message = "Could not bring Microsoft Edge to the front."; return false; }

            Thread.Sleep(150);
            keybd_event(VK_F5, 0, 0, UIntPtr.Zero);
            keybd_event(VK_F5, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(150);

            if (previous != IntPtr.Zero && previous != window) BringToFront(previous);

            message = $"Refreshed the active Edge tab ({GetTitle(window)}).";
            return true;
        }

        private static IntPtr FindTopEdgeWindow(HashSet<uint> pids)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (!pids.Contains(pid)) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "Chrome_WidgetWin_1" || GetTitle(h).Length == 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static bool BringToFront(IntPtr window)
        {
            if (IsIconic(window)) ShowWindow(window, SW_RESTORE);

            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var currentThread = GetCurrentThreadId();
            bool attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }

            for (int i = 0; i < 10 && GetForegroundWindow() != window; i++) Thread.Sleep(30);
            return GetForegroundWindow() == window;
        }

        private static string GetTitle(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private const byte VK_F5 = 0x74;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int SW_RESTORE = 9;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }
}
