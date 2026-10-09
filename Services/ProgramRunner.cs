using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    public static class ProgramRunner
    {
        private static readonly object ConsoleLock = new();

        public static string ConsoleLogFolder => Path.Combine(AppSettings.DataFolder, "console-logs");

        public static string ConsoleLogFile(UpdateProfile p) =>
            Path.Combine(ConsoleLogFolder, string.Concat(p.DisplayName.Split(Path.GetInvalidFileNameChars())) + ".log");

        public static string? ExePath(UpdateProfile p) =>
            string.IsNullOrWhiteSpace(p.ExeName) || string.IsNullOrWhiteSpace(p.LocalFolder)
                ? null
                : Path.Combine(Path.GetFullPath(p.LocalFolder), p.ExeName);

        public static bool? IsConsoleExe(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt16() != 0x5A4D) return null;
                fs.Position = 0x3C;
                var peOffset = br.ReadInt32();
                fs.Position = peOffset;
                if (br.ReadUInt32() != 0x00004550) return null;
                fs.Position = peOffset + 4 + 20 + 68;
                var subsystem = br.ReadUInt16();
                return subsystem == 3;
            }
            catch { return null; }
        }

        public static void Start(UpdateProfile p, string exePath)
        {
            var name = p.DisplayName;
            if (!File.Exists(exePath)) { Logger.Warn($"[{name}] Cannot start, file not found: {exePath}"); return; }
            var dir = Path.GetDirectoryName(exePath)!;
            var args = p.StartArguments?.Trim() ?? "";
            try
            {
                if (!p.IsConsoleApp)
                {
                    Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = true, WorkingDirectory = dir });
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)}.");
                }
                else if (p.HideConsole)
                {
                    StartHiddenConsole(p, exePath, args, dir);
                }
                else if (p.KeepConsoleOpen)
                {
                    var command = $"/k \"\"{exePath}\"{(args.Length > 0 ? " " + args : "")}\"";
                    Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", command)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = dir
                    });
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} in a console window that stays open.");
                }
                else
                {
                    Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = true, WorkingDirectory = dir });
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} in a console window.");
                }
            }
            catch (Exception ex) { Logger.Error($"[{name}] Could not start the program: " + ex.Message); }
        }

        private static void StartHiddenConsole(UpdateProfile p, string exePath, string args, string dir)
        {
            Directory.CreateDirectory(ConsoleLogFolder);
            var logFile = ConsoleLogFile(p);
            try
            {
                if (File.Exists(logFile) && new FileInfo(logFile).Length > 5 * 1024 * 1024)
                    File.Move(logFile, logFile + ".old", true);
            }
            catch { }

            try { File.AppendAllText(logFile, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} started {exePath} {args}".TrimEnd() + Environment.NewLine); }
            catch { }

            var command = $"/c \"\"{exePath}\"{(args.Length > 0 ? " " + args : "")} >> \"{logFile}\" 2>&1\"";
            Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            })?.Dispose();
            Logger.Info($"[{p.DisplayName}] Started {Path.GetFileName(exePath)}{ArgsText(args)} hidden; output goes to {logFile}");
        }

        public static async Task<bool> CloseAsync(UpdateProfile profile, string exePath)
        {
            var profileName = profile.DisplayName;
            var name = Path.GetFileNameWithoutExtension(exePath);
            var procs = Process.GetProcessesByName(name).Where(p => p.Id != Environment.ProcessId && IsSameExe(p, exePath)).ToList();
            if (procs.Count == 0) { Logger.Info($"[{profileName}] {name} is not running."); return false; }

            Logger.Info($"[{profileName}] Closing {name} ({procs.Count} process(es))...");
            bool console = profile.IsConsoleApp || IsConsoleExe(exePath) == true;
            foreach (var p in procs)
            {
                if (console) await Task.Run(() => SendCtrlC(p.Id));
                else { try { p.CloseMainWindow(); } catch { } }
            }

            for (int t = 0; t < 50 && procs.Any(p => !HasExited(p)); t++) await Task.Delay(100);

            foreach (var p in procs.Where(p => !HasExited(p)))
            {
                try { p.Kill(entireProcessTree: true); Logger.Warn($"[{profileName}] {name} did not close in time and was terminated."); }
                catch (Win32Exception ex) { throw new InvalidOperationException($"Could not close {name}: {ex.Message}"); }
            }
            foreach (var p in procs) { try { p.WaitForExit(10000); } catch { } p.Dispose(); }
            await Task.Delay(500);
            return true;
        }

        private static void SendCtrlC(int pid)
        {
            lock (ConsoleLock)
            {
                FreeConsole();
                if (!AttachConsole((uint)pid)) return;
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                try
                {
                    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                    Thread.Sleep(300);
                }
                finally
                {
                    FreeConsole();
                    Thread.Sleep(100);
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                }
            }
        }

        private static bool HasExited(Process p) { try { return p.HasExited; } catch { return true; } }

        private static bool IsSameExe(Process p, string exePath)
        {
            try
            {
                var path = p.MainModule?.FileName;
                return path == null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private static string ArgsText(string args) => args.Length > 0 ? $" with arguments \"{args}\"" : "";

        private const uint CTRL_C_EVENT = 0;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    }
}
