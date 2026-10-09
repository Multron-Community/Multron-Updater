using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace MultronUpdater.Services
{
    public static class StartupHelper
    {
        private const string TaskName = "MultronUpdater";

        public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

        public static void Set(bool enable)
        {
            if (!enable)
            {
                if (IsEnabled()) RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out _);
                return;
            }

            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the program path.");
            var user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Multron Updater - automatic GitHub updater</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT15S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>--tray</Arguments>
      <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
            var tmp = Path.Combine(Path.GetTempPath(), "MultronUpdater_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", out var output) != 0)
                    throw new InvalidOperationException("Could not create the startup task: " + output.Trim());
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static int RunSchtasks(string args, out string output)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit(15000);
            output = stdout.Result + stderr.Result;
            return p.ExitCode;
        }
    }
}
