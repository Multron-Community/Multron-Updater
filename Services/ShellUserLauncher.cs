using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MultronUpdater.Services
{
    public static class ShellUserLauncher
    {
        public static bool IsElevated
        {
            get
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static void Start(string exePath, string arguments, string workingDirectory, bool noWindow = false)
        {
            if (!IsElevated)
            {
                Process.Start(new ProcessStartInfo(exePath, arguments)
                {
                    UseShellExecute = !noWindow,
                    CreateNoWindow = noWindow,
                    WorkingDirectory = workingDirectory
                })?.Dispose();
                return;
            }
            var handle = CreateAsShellUser(exePath, arguments, workingDirectory, noWindow);
            CloseHandle(handle);
        }

        private static IntPtr CreateAsShellUser(string exePath, string arguments, string workingDirectory, bool noWindow)
        {
            var shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero) throw new InvalidOperationException("The Windows shell (Explorer) is not running.");
            GetWindowThreadProcessId(shellWindow, out var shellPid);

            IntPtr shellProcess = IntPtr.Zero, shellToken = IntPtr.Zero, userToken = IntPtr.Zero;
            try
            {
                shellProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
                if (shellProcess == IntPtr.Zero) throw new Win32Exception();
                if (!OpenProcessToken(shellProcess, TOKEN_DUPLICATE, out shellToken)) throw new Win32Exception();
                const uint access = TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
                if (!DuplicateTokenEx(shellToken, access, IntPtr.Zero, 2, 1, out userToken)) throw new Win32Exception();

                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                var commandLine = $"\"{exePath}\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
                if (!CreateProcessWithTokenW(userToken, 0, null, commandLine, noWindow ? CREATE_NO_WINDOW : 0, IntPtr.Zero, workingDirectory, ref si, out var pi))
                    throw new Win32Exception();
                CloseHandle(pi.hThread);
                return pi.hProcess;
            }
            finally
            {
                if (userToken != IntPtr.Zero) CloseHandle(userToken);
                if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
                if (shellProcess != IntPtr.Zero) CloseHandle(shellProcess);
            }
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        private const uint CREATE_NO_WINDOW = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? application, string commandLine, uint creationFlags,
                                                           IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
