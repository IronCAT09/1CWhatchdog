using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    /// <summary>
    /// Автозапуск через Планировщик заданий: задание при входе пользователя
    /// с наивысшими правами — так программа стартует без запроса UAC
    /// и может запускать службы. Без ограничения времени работы и питания от батареи.
    /// </summary>
    static class Autostart
    {
        const string TaskName = Program.AppName;

        const string TaskXml =
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Монитор служб Windows</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{0}</UserId><Delay>PT15S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author""><UserId>{0}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{1}</Command><WorkingDirectory>{2}</WorkingDirectory></Exec>
  </Actions>
</Task>";

        public static bool IsEnabled()
        {
            string output;
            return Run("/Query /TN \"" + TaskName + "\"", out output) == 0;
        }

        public static void Enable()
        {
            string exe = Application.ExecutablePath;
            string xml = string.Format(TaskXml,
                SecurityElement.Escape(WindowsIdentity.GetCurrent().Name),
                SecurityElement.Escape(exe),
                SecurityElement.Escape(Path.GetDirectoryName(exe)));

            string tmp = Path.Combine(Path.GetTempPath(), TaskName + "_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                Check(Run("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F", out xml), xml);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        public static void Disable()
        {
            string output;
            Check(Run("/Delete /TN \"" + TaskName + "\" /F", out output), output);
        }

        static void Check(int exitCode, string output)
        {
            if (exitCode != 0)
                throw new InvalidOperationException(output.Trim());
        }

        static int Run(string args, out string output)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                output = stderr.Length > 0 ? stderr : stdout;
                return p.ExitCode;
            }
        }
    }
}
