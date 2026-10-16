using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultronUpdater.Services
{
    public sealed record BuildResult(bool Success, int ExitCode, bool TimedOut, TimeSpan Duration, IReadOnlyList<string> Tail);

    public static class BuildRunner
    {
        public static string LogFolder => Path.Combine(AppSettings.DataFolder, "build-logs");
        public static string ScriptFolder => Path.Combine(AppSettings.DataFolder, "build-scripts");

        public static string LogFile(UpdateProfile p) =>
            Path.Combine(LogFolder, string.Concat(p.DisplayName.Split(Path.GetInvalidFileNameChars())) + ".log");

        public static readonly (string Name, BuildShell Shell, string Commands, string Output)[] Templates =
        {
            (".NET (dotnet publish, single exe)", BuildShell.Cmd,
                "dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish", "publish"),
            (".NET (dotnet build)", BuildShell.Cmd,
                "dotnet build -c Release -o build-output", "build-output"),
            (".NET Framework / C++ (MSBuild)", BuildShell.Cmd,
                "for /f \"usebackq delims=\" %%i in (`\"%ProgramFiles(x86)%\\Microsoft Visual Studio\\Installer\\vswhere.exe\" -latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe`) do set MSBUILD=%%i\r\n" +
                "\"%MSBUILD%\" /restore /p:Configuration=Release /p:OutDir=%MU_SOURCE%\\build-output\\ /m", "build-output"),
            ("Node.js (npm)", BuildShell.Cmd,
                "call npm ci\r\ncall npm run build", "dist"),
            ("Python (PyInstaller)", BuildShell.Cmd,
                "python -m pip install -r requirements.txt pyinstaller\r\npython -m PyInstaller --noconfirm --onefile --distpath dist main.py", "dist"),
            ("Rust (cargo)", BuildShell.Cmd,
                "cargo build --release", @"target\release"),
            ("Go", BuildShell.Cmd,
                "go build -o build-output\\ ./...", "build-output"),
            ("CMake", BuildShell.Cmd,
                "cmake -S . -B build -DCMAKE_BUILD_TYPE=Release\r\ncmake --build build --config Release", @"build\Release"),
            ("PowerShell script in the repository (build.ps1)", BuildShell.WindowsPowerShell,
                "& .\\build.ps1", "publish"),
            ("Batch file in the repository (build.bat)", BuildShell.Cmd,
                "call build.bat", "publish"),
        };

        public static int? DetectTemplate(string sourceFolder)
        {
            if (!Directory.Exists(sourceFolder)) return null;
            bool Has(string pattern) =>
                Directory.EnumerateFiles(sourceFolder, pattern, SearchOption.TopDirectoryOnly).Any();
            if (Has("build.ps1")) return 8;
            if (Has("build.bat") || Has("build.cmd")) return 9;
            if (Has("*.csproj") || Has("*.sln") || Has("*.slnx") || Has("*.fsproj"))
            {
                var proj = Directory.EnumerateFiles(sourceFolder, "*.csproj").FirstOrDefault();
                if (proj != null && !File.ReadAllText(proj).Contains("Sdk=", StringComparison.OrdinalIgnoreCase)) return 2;
                return 0;
            }
            if (Has("*.vcxproj")) return 2;
            if (Has("package.json")) return 3;
            if (Has("Cargo.toml")) return 5;
            if (Has("go.mod")) return 6;
            if (Has("CMakeLists.txt")) return 7;
            if (Has("requirements.txt") || Has("pyproject.toml") || Has("*.spec")) return 4;
            return null;
        }

        public static async Task<BuildResult> RunAsync(UpdateProfile p, string sourceFolder, string version,
                                                       Action<string>? onLine, CancellationToken ct)
        {
            Directory.CreateDirectory(LogFolder);
            Directory.CreateDirectory(ScriptFolder);
            var workDir = string.IsNullOrWhiteSpace(p.BuildWorkingDir)
                ? sourceFolder
                : Path.GetFullPath(Path.Combine(sourceFolder, p.BuildWorkingDir.Trim()));
            if (!Directory.Exists(workDir))
                throw new InvalidOperationException($"Build working folder '{workDir}' does not exist.");

            var (exe, args, scriptPath) = WriteScript(p);
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["MU_SOURCE"] = sourceFolder;
            psi.Environment["MU_OUTPUT"] = Path.GetFullPath(Path.Combine(sourceFolder, p.BuildOutputPath.Trim()));
            psi.Environment["MU_TARGET"] = Path.GetFullPath(p.LocalFolder);
            psi.Environment["MU_TARGET_NAME"] = p.DisplayName;
            psi.Environment["MU_VERSION"] = version;
            psi.Environment["MU_EXE"] = p.ExeName;
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            foreach (var raw in (p.BuildEnvironment ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                psi.Environment[line[..eq].Trim()] = Environment.ExpandEnvironmentVariables(line[(eq + 1)..].Trim());
            }

            var logFile = LogFile(p);
            if (File.Exists(logFile)) { try { File.Copy(logFile, logFile + ".prev", true); } catch { } }
            var tail = new Queue<string>();
            var logLock = new object();
            using var log = new StreamWriter(logFile, false, new UTF8Encoding(false)) { AutoFlush = true };
            log.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} build of {p.DisplayName} ({version})");
            log.WriteLine($"===== {exe} {args}");
            log.WriteLine($"===== in {workDir}");

            void Line(string? text)
            {
                if (text == null) return;
                lock (logLock)
                {
                    log.WriteLine(text);
                    tail.Enqueue(text);
                    while (tail.Count > 40) tail.Dequeue();
                }
                if (text.Trim().Length > 0) onLine?.Invoke(text.Trim());
            }

            var watch = Stopwatch.StartNew();
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => Line(e.Data);
            proc.ErrorDataReceived += (_, e) => Line(e.Data);
            try { proc.Start(); }
            catch (Exception ex) { throw new InvalidOperationException($"Could not start {exe}: {ex.Message}"); }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var timeout = TimeSpan.FromMinutes(Math.Clamp(p.BuildTimeoutMinutes, 1, 24 * 60));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            bool timedOut = false;
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.WaitForExit(5000); } catch { }
                if (!timedOut) { log.WriteLine("===== cancelled"); throw; }
            }
            proc.WaitForExit();
            watch.Stop();

            int code = timedOut ? -1 : proc.ExitCode;
            log.WriteLine(timedOut
                ? $"===== timed out after {timeout.TotalMinutes:0} min"
                : $"===== exit code {code} after {watch.Elapsed:mm\\:ss}");
            try { File.Delete(scriptPath); } catch { }
            List<string> lines;
            lock (logLock) lines = tail.ToList();
            return new BuildResult(!timedOut && code == 0, code, timedOut, watch.Elapsed, lines);
        }

        private static (string Exe, string Args, string ScriptPath) WriteScript(UpdateProfile p)
        {
            var lines = (p.BuildCommands ?? "").Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            string path;
            switch (p.BuildShell)
            {
                case BuildShell.Cmd:
                    path = Path.Combine(ScriptFolder, p.Id + ".cmd");
                    sb.AppendLine("@echo off");
                    sb.AppendLine("chcp 65001 >nul");
                    int depth = 0;
                    foreach (var raw in lines)
                    {
                        var line = raw.TrimEnd();
                        sb.AppendLine(line);
                        var t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("::") || t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
                        depth += Count(t, '(') - Count(t, ')');
                        if (p.BuildStopOnError && depth <= 0 && !t.EndsWith('^'))
                            sb.AppendLine("if errorlevel 1 exit /b %errorlevel%");
                    }
                    sb.AppendLine("exit /b 0");
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                    return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /c \"\"{path}\"\"", path);

                default:
                    path = Path.Combine(ScriptFolder, p.Id + ".ps1");
                    sb.AppendLine("$ErrorActionPreference = 'Stop'");
                    sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
                    sb.AppendLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
                    sb.AppendLine("$global:LASTEXITCODE = 0");
                    sb.AppendLine("try {");
                    int ps = 0;
                    foreach (var raw in lines)
                    {
                        var line = raw.TrimEnd();
                        sb.AppendLine(line);
                        var t = line.Trim();
                        if (t.Length == 0 || t.StartsWith('#')) continue;
                        ps += Count(t, '{') + Count(t, '(') + Count(t, '[') - Count(t, '}') - Count(t, ')') - Count(t, ']');
                        if (p.BuildStopOnError && ps <= 0 && !t.EndsWith('`') && !t.EndsWith('|') && !t.EndsWith(','))
                            sb.AppendLine("if ($LASTEXITCODE) { exit $LASTEXITCODE }");
                    }
                    sb.AppendLine("} catch { Write-Error $_; exit 1 }");
                    sb.AppendLine("exit $LASTEXITCODE");
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                    var exe = p.BuildShell == BuildShell.PowerShell7 ? "pwsh.exe" : "powershell.exe";
                    return (exe, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"", path);
            }
        }

        private static int Count(string s, char c) => s.Count(x => x == c);
    }
}
