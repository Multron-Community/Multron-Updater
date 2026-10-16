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

        public static string? ExePath(UpdateProfile p)
        {
            if (string.IsNullOrWhiteSpace(p.ExeName) || string.IsNullOrWhiteSpace(p.LocalFolder)) return null;
            if (!HasWildcard(p.ExeName)) return Path.Combine(Path.GetFullPath(p.LocalFolder), p.ExeName.Trim());
            return MatchingExePaths(p).FirstOrDefault();
        }

        public static bool HasWildcard(string? exeName) => exeName != null && exeName.IndexOfAny(new[] { '*', '?' }) >= 0;

        public static bool MatchesExeName(UpdateProfile p, string fileName) =>
            HasWildcard(p.ExeName) && UpdateService.MatchesPattern(Path.GetFileName(fileName), Path.GetFileName(p.ExeName.Trim()));

        public static System.Collections.Generic.List<string> MatchingExePaths(UpdateProfile p)
        {
            var result = new System.Collections.Generic.List<string>();
            if (!HasWildcard(p.ExeName) || string.IsNullOrWhiteSpace(p.LocalFolder)) return result;
            var name = p.ExeName.Trim().Replace('/', Path.DirectorySeparatorChar);
            var dir = Path.Combine(Path.GetFullPath(p.LocalFolder), Path.GetDirectoryName(name) ?? "");
            if (!Directory.Exists(dir)) return result;
            try
            {
                result.AddRange(Directory.EnumerateFiles(dir, Path.GetFileName(name))
                    .Where(f => MatchesExeName(p, f))
                    .OrderByDescending(File.GetLastWriteTimeUtc));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

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

        /// <param name="extraArgs">Added after the profile's arguments (e.g. <c>--handoff</c>).</param>
        public static void Start(UpdateProfile p, string exePath, string extraArgs = "")
        {
            var name = p.DisplayName;
            if (!File.Exists(exePath)) { Logger.Warn($"[{name}] Cannot start, file not found: {exePath}"); return; }
            var dir = Path.GetDirectoryName(exePath)!;
            var args = ((p.StartArguments?.Trim() ?? "") + " " + extraArgs).Trim();
            var asUser = p.StartWithoutAdmin && ShellUserLauncher.IsElevated ? " as a normal user (without administrator rights)" : "";
            try
            {
                if (!p.IsConsoleApp)
                {
                    Launch(p, exePath, args, dir, noWindow: false);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)}{asUser}.");
                }
                else if (p.HideConsole)
                {
                    StartHiddenConsole(p, exePath, args, dir);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} hidden{asUser}; output goes to {ConsoleLogFile(p)}");
                }
                else if (p.KeepConsoleOpen)
                {
                    var command = $"/k \"\"{exePath}\"{(args.Length > 0 ? " " + args : "")}\"";
                    Launch(p, ComSpec, command, dir, noWindow: false);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} in a console window that stays open{asUser}.");
                }
                else
                {
                    Launch(p, exePath, args, dir, noWindow: false);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} in a console window{asUser}.");
                }
            }
            catch (Exception ex) { Logger.Error($"[{name}] Could not start the program: " + ex.Message); }
        }

        private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        private static void Launch(UpdateProfile p, string file, string args, string dir, bool noWindow)
        {
            if (p.StartWithoutAdmin)
            {
                ShellUserLauncher.Start(file, args, dir, noWindow);
                return;
            }
            Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = !noWindow,
                CreateNoWindow = noWindow,
                WorkingDirectory = dir
            })?.Dispose();
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
            Launch(p, ComSpec, command, dir, noWindow: true);
        }

        public static async Task<bool> CloseAsync(UpdateProfile profile, string exePath)
        {
            var profileName = profile.DisplayName;
            var name = Path.GetFileNameWithoutExtension(exePath);
            var procs = Process.GetProcessesByName(name).Where(p => p.Id != Environment.ProcessId && IsSameExe(p, exePath)).ToList();
            if (procs.Count == 0) { Logger.Info($"[{profileName}] {name} is not running."); return false; }

            Logger.Info($"[{profileName}] Closing {name} ({procs.Count} process(es))...");
            bool console = profile.IsConsoleApp || IsConsoleExe(exePath) == true;
            await CloseProcessesAsync(profileName, name, procs, console);
            return true;
        }

        public static async Task<bool> CloseAllAsync(UpdateProfile profile, string? exePath)
        {
            if (!HasWildcard(profile.ExeName)) return exePath != null && await CloseAsync(profile, exePath);
            var matches = MatchingExePaths(profile);
            if (matches.Count == 0) { Logger.Info($"[{profile.DisplayName}] No file matching {profile.ExeName} to close."); return false; }
            bool any = false;
            foreach (var path in matches) any |= await CloseAsync(profile, path);
            return any;
        }

        /// <summary>
        /// Closes one process by id, the same way (Ctrl+C or close the window, then
        /// terminate after 5 s). Used when its exe was renamed for a seamless update.
        /// False when it is not running.
        /// </summary>
        public static async Task<bool> CloseByIdAsync(UpdateProfile profile, int pid)
        {
            Process proc;
            try { proc = Process.GetProcessById(pid); }
            catch (ArgumentException) { return false; }
            var name = proc.ProcessName;
            bool console = profile.IsConsoleApp;
            try { console |= proc.MainModule?.FileName is string f && IsConsoleExe(f) == true; } catch { }
            Logger.Info($"[{profile.DisplayName}] Closing {name} (pid {pid})...");
            await CloseProcessesAsync(profile.DisplayName, name, new() { proc }, console);
            return true;
        }

        private static async Task CloseProcessesAsync(string profileName, string name, System.Collections.Generic.List<Process> procs, bool console)
        {
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
