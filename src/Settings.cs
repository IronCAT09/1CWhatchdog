using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ServiceWatchdog
{
    /// <summary>
    /// Настройки и журнал в C:\ProgramData\ServiceWatchdog. Изменять их могут только
    /// администраторы и SYSTEM, остальные пользователи — только читать.
    /// </summary>
    static class Settings
    {
        const long MaxLogSize = 1024 * 1024;

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Program.AppName);
        public static readonly string LogPath = Path.Combine(Dir, "watchdog.log");
        static readonly string ServicesPath = Path.Combine(Dir, "services.txt");
        static readonly string TimeoutPath = Path.Combine(Dir, "timeout.txt");
        static readonly object logSync = new object();

        /// <summary>
        /// Создаёт папку и закрывает её на запись для обычных пользователей: список служб
        /// исполняет мониторинг с правами SYSTEM. По умолчанию в подпапках ProgramData
        /// пользователи могут создавать файлы.
        /// </summary>
        public static void EnsureDirectory()
        {
            Directory.CreateDirectory(Dir);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(Dir, security);
        }

        /// <summary>Отпечаток файлов настроек — по нему видно, что их изменили.</summary>
        public static string GetStamp()
        {
            return Stamp(ServicesPath) + "|" + Stamp(TimeoutPath);
        }

        static long Stamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        public static List<string> LoadServices()
        {
            if (!File.Exists(ServicesPath))
                return new List<string>();
            return File.ReadAllLines(ServicesPath, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void SaveServices(IEnumerable<string> services)
        {
            EnsureDirectory();
            string tmp = ServicesPath + ".tmp";
            File.WriteAllLines(tmp, services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), Encoding.UTF8);
            if (File.Exists(ServicesPath))
                File.Replace(tmp, ServicesPath, null);
            else
                File.Move(tmp, ServicesPath);
        }

        public static int LoadTimeout()
        {
            int seconds;
            if (File.Exists(TimeoutPath) && int.TryParse(File.ReadAllText(TimeoutPath).Trim(), out seconds))
                return seconds;
            return ServiceMonitor.DefaultTimeoutSeconds;
        }

        public static void SaveTimeout(int seconds)
        {
            EnsureDirectory();
            File.WriteAllText(TimeoutPath, seconds.ToString());
        }

        public static void AppendLog(LogEntry entry)
        {
            lock (logSync)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxLogSize)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, entry.Format() + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
