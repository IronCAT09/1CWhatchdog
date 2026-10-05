using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    /// <summary>
    /// Установка автозапуска (нужны права администратора). Создаёт два задания Планировщика:
    ///   «ServiceWatchdog Monitor» — мониторинг от имени SYSTEM при загрузке Windows;
    ///   «ServiceWatchdog Tray»    — значок в трее при входе любого пользователя (без повышения прав).
    /// Exe копируется в Program Files: оттуда его может запустить любой пользователь,
    /// и подменить файл, исполняемый от имени SYSTEM, может только администратор.
    /// </summary>
    static class Autostart
    {
        const string MonitorTask = "ServiceWatchdog Monitor";
        const string TrayTask = "ServiceWatchdog Tray";
        const string LegacyTask = "ServiceWatchdog"; // автозапуск версии 1.0 — только для текущего пользователя
        static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

        const string SettingsXml = @"
  <Settings>
    <MultipleInstancesPolicy>{0}</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>{1}</Priority>{2}
  </Settings>";

        const string TaskXml =
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>{0}</Description></RegistrationInfo>
  <Triggers>{1}</Triggers>
  <Principals><Principal id=""Author"">{2}</Principal></Principals>{3}
  <Actions Context=""Author"">
    <Exec><Command>{4}</Command>{5}<WorkingDirectory>{6}</WorkingDirectory></Exec>
  </Actions>
</Task>";

        public static string InstallDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.AppName);
            }
        }

        public static bool IsEnabled()
        {
            string output;
            return Run("/Query /TN \"" + MonitorTask + "\"", out output) == 0;
        }

        /// <summary>Устанавливает программу и запускает мониторинг. Возвращает путь к установленному exe.</summary>
        public static string Install()
        {
            StopMonitor();
            string exe = DeployExecutable();
            string dir = Path.GetDirectoryName(exe);
            Settings.EnsureDirectory();

            Register(MonitorTask, string.Format(TaskXml,
                "Монитор служб: перезапуск отмеченных служб Windows",
                "<BootTrigger><Enabled>true</Enabled></BootTrigger>",
                "<UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel>",
                string.Format(SettingsXml, "IgnoreNew", 5,
                    "\n    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>"),
                SecurityElement.Escape(exe), "<Arguments>/monitor</Arguments>", SecurityElement.Escape(dir)));

            // GroupId S-1-5-32-545 («Пользователи») и триггер без UserId — вход любого пользователя.
            Register(TrayTask, string.Format(TaskXml,
                "Монитор служб: значок в трее",
                "<LogonTrigger><Enabled>true</Enabled><Delay>PT10S</Delay></LogonTrigger>",
                "<GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel>",
                string.Format(SettingsXml, "Parallel", 7, ""),
                SecurityElement.Escape(exe), "", SecurityElement.Escape(dir)));

            string output;
            Run("/Delete /TN \"" + LegacyTask + "\" /F", out output);
            Check(Run("/Run /TN \"" + MonitorTask + "\"", out output), output);
            return exe;
        }

        public static void Uninstall()
        {
            StopMonitor();
            string output;
            Check(Run("/Delete /TN \"" + MonitorTask + "\" /F", out output), output);
            Run("/Delete /TN \"" + TrayTask + "\" /F", out output);
            Run("/Delete /TN \"" + LegacyTask + "\" /F", out output);
        }

        static void StopMonitor()
        {
            string output;
            Run("/End /TN \"" + MonitorTask + "\"", out output);
            // Задание с IgnoreNew не запустится повторно, пока старый процесс не завершился.
            var deadline = DateTime.UtcNow + StopTimeout;
            while (MonitorHost.IsRunning() && DateTime.UtcNow < deadline)
                Thread.Sleep(250);
        }

        static string DeployExecutable()
        {
            string source = Application.ExecutablePath;
            string target = Path.Combine(InstallDir, Program.AppName + ".exe");
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return target;

            Directory.CreateDirectory(InstallDir);
            if (File.Exists(target))
            {
                try
                {
                    File.Delete(target);
                }
                catch (UnauthorizedAccessException)
                {
                    MoveAside(target); // exe запущен (значок в трее у кого-то из пользователей)
                }
                catch (IOException)
                {
                    MoveAside(target);
                }
            }
            File.Copy(source, target);
            return target;
        }

        static void MoveAside(string path)
        {
            // Запущенный exe нельзя удалить, но можно переименовать.
            string old = path + ".old";
            try { if (File.Exists(old)) File.Delete(old); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            if (File.Exists(old))
                old = path + "." + DateTime.Now.Ticks + ".old";
            File.Move(path, old);
        }

        static void Register(string name, string xml)
        {
            string tmp = Path.Combine(Path.GetTempPath(), Program.AppName + "_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                string output;
                Check(Run("/Create /TN \"" + name + "\" /XML \"" + tmp + "\" /F", out output), output);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        static void Check(int exitCode, string output)
        {
            if (exitCode != 0)
                throw new InvalidOperationException(output.Trim());
        }

        static int Run(string args, out string output)
        {
            var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding
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
