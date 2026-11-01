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
        /// <returns>The started process (for <see cref="StoppedRightAwayAsync"/>); null when it did not start.</returns>
        public static Launched? Start(UpdateProfile p, string exePath, string extraArgs = "")
        {
            var name = p.DisplayName;
            if (!File.Exists(exePath)) { Logger.Warn($"[{name}] Cannot start, file not found: {exePath}"); return null; }
            var dir = Path.GetDirectoryName(exePath)!;
            var args = ((p.StartArguments?.Trim() ?? "") + " " + extraArgs).Trim();
            var asUser = p.StartWithoutAdmin && ShellUserLauncher.IsElevated ? " as a normal user (without administrator rights)" : "";
            try
            {
                Launched? l;
                if (!p.IsConsoleApp)
                {
                    l = Launch(p, exePath, args, dir, noWindow: false);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)}{asUser}.");
                }
                else if (p.HideConsole)
                {
                    l = StartHiddenConsole(p, exePath, args, dir);
                    if (l != null) l.ConsoleLog = ConsoleLogFile(p);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} hidden{asUser}; output goes to {ConsoleLogFile(p)}");
                }
                else if (p.KeepConsoleOpen)
                {
                    var command = $"/k \"\"{exePath}\"{(args.Length > 0 ? " " + args : "")}\"";
                    l = Launch(p, ComSpec, command, dir, noWindow: false);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} in a console window that stays open{asUser}.");
                }
                else
                {
                    l = Launch(p, exePath, args, dir, noWindow: false);
                    Logger.Info($"[{name}] Started {Path.GetFileName(exePath)}{ArgsText(args)} in a console window{asUser}.");
                }
                if (l != null) l.ExePath = exePath;
                return l;
            }
            catch (Exception ex) { Logger.Error($"[{name}] Could not start the program: " + ex.Message); return null; }
        }

        /// <summary>
        /// Watches a program that was just started for <paramref name="watch"/>. Null while it
        /// keeps running (or cannot be watched); otherwise why it stopped (<see cref="StopReason"/>).
        /// </summary>
        public static async Task<string?> StoppedRightAwayAsync(Launched? l, TimeSpan watch, CancellationToken ct = default)
        {
            if (l == null) return null;
            var until = DateTime.UtcNow + watch;
            while (DateTime.UtcNow < until)
            {
                if (l.HasExited) return StopReason(l);
                await Task.Delay(500, ct);
            }
            return l.HasExited ? StopReason(l) : null;
        }

        /// <summary>Written by Multron server next to its exe when it stops on an error.</summary>
        public const string ServerExitLog = "multron_server_exit.log";

        /// <summary>
        /// Why a started program stopped: its exit code, the messages Multron server wrote
        /// to <see cref="ServerExitLog"/> since it was started, otherwise the end of its
        /// hidden console output. The console window of a visible start closes with the
        /// message, so this is the only place it is still seen.
        /// </summary>
        public static string StopReason(Launched l)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (l.ExitCode is int code) parts.Add(ExitCodeText(code));
            var messages = ExitLogMessages(l);
            if (messages.Count > 0) parts.Add(string.Join(Environment.NewLine, messages));
            else if (ConsoleTail(l) is string tail) parts.Add("last output:" + Environment.NewLine + tail);
            return parts.Count > 0 ? string.Join(": ", parts) : "no message (start it in a console window that stays open to see its output)";
        }

        /// <summary>
        /// Exit code with what it means for the codes Windows sets when a program dies
        /// before it can write anything (a missing DLL closes the console at once).
        /// </summary>
        private static string ExitCodeText(int code) => unchecked((uint)code) switch
        {
            101 => "exit code 101 (the program crashed)",
            0xC0000135 => "a DLL it needs is missing (0xC0000135); compare the DLLs next to the exe with the release",
            0xC0000139 => "a DLL next to the exe is the wrong version (0xC0000139, entry point not found)",
            0xC000007B => "a DLL next to the exe is for the wrong platform (0xC000007B, 32-bit and 64-bit mixed)",
            0xC0000005 => "the program crashed (0xC0000005, access violation)",
            0xC00000FD => "the program crashed (0xC00000FD, stack overflow)",
            0xC0000409 => "the program aborted (0xC0000409)",
            var u when u >= 0xC0000000 => $"the program crashed (0x{u:X8})",
            _ => $"exit code {code}"
        };

        private static System.Collections.Generic.List<string> ExitLogMessages(Launched l)
        {
            var result = new System.Collections.Generic.List<string>();
            if (l.ExePath == null) return result;
            var file = Path.Combine(Path.GetDirectoryName(l.ExePath)!, ServerExitLog);
            if (!File.Exists(file)) return result;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var since = l.StartedUtc.AddSeconds(-2);
                while (reader.ReadLine() is string line)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("at", out var at) && at.TryGetDateTime(out var when) && when.ToUniversalTime() >= since &&
                            root.TryGetProperty("message", out var msg) && msg.GetString() is string m)
                            result.Add(m);
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // A crash can write a few; the last ones tell why it stopped.
            return result.Count > 5 ? result.GetRange(result.Count - 5, 5) : result;
        }

        private static string? ConsoleTail(Launched l)
        {
            if (l.ConsoleLog == null || !File.Exists(l.ConsoleLog)) return null;
            try
            {
                using var fs = new FileStream(l.ConsoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(Math.Max(0, fs.Length - 64 * 1024), SeekOrigin.Begin);
                var lines = new StreamReader(fs).ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).ToList();
                // Only this start's output: after its "===== ... started" line.
                var header = lines.FindLastIndex(x => x.StartsWith("===== "));
                var own = lines.Skip(header + 1).Where(x => x.Trim().Length > 0).ToList();
                return own.Count == 0 ? null : string.Join(Environment.NewLine, own.Skip(Math.Max(0, own.Count - 15)));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        private static Launched? Launch(UpdateProfile p, string file, string args, string dir, bool noWindow)
        {
            if (p.StartWithoutAdmin) return ShellUserLauncher.Start(file, args, dir, noWindow);
            var proc = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = !noWindow,
                CreateNoWindow = noWindow,
                WorkingDirectory = dir
            });
            return proc == null ? null : new Launched(proc);
        }

        private static Launched? StartHiddenConsole(UpdateProfile p, string exePath, string args, string dir)
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
            return Launch(p, ComSpec, command, dir, noWindow: true);
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

    /// <summary>
    /// A program this updater started (or the console that runs it): whether it still
    /// runs and how it ended. Holds the process handle, so the exit code stays readable.
    /// </summary>
    public sealed class Launched : IDisposable
    {
        private readonly Process? _proc;
        private IntPtr _handle;

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public string? ExePath { get; set; }
        /// <summary>Where a hidden console's output goes.</summary>
        public string? ConsoleLog { get; set; }

        internal Launched(Process proc) => _proc = proc;
        internal Launched(IntPtr processHandle) => _handle = processHandle;

        public bool HasExited
        {
            get
            {
                try
                {
                    if (_proc != null) return _proc.HasExited;
                    return _handle != IntPtr.Zero && WaitForSingleObject(_handle, 0) == 0;
                }
                catch { return false; }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    if (_proc != null) return _proc.HasExited ? _proc.ExitCode : null;
                    if (_handle != IntPtr.Zero && GetExitCodeProcess(_handle, out var c) && c != StillActive) return unchecked((int)c);
                }
                catch { }
                return null;
            }
        }

        public void Dispose()
        {
            _proc?.Dispose();
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        }

        private const uint StillActive = 259;

        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
