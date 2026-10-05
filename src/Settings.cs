using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OneCWhatchdog
{
    /// <summary>
    /// Настройки и журнал в C:\ProgramData\1CWhatchdog. Изменять их могут только
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
        static readonly string AllowedPath = Path.Combine(Dir, "allowed.txt");
        static readonly string DeniedPath = Path.Combine(Dir, "denied.txt");
        static readonly string AppControlPath = Path.Combine(Dir, "appcontrol.txt");
        /// <summary>Папка настроек версий до переименования (ServiceWatchdog 1.0–1.1).</summary>
        static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServiceWatchdog");
        static readonly object logSync = new object();

        /// <summary>
        /// Создаёт папку и закрывает её на запись для обычных пользователей: списки служб и
        /// разрешённых программ исполняет мониторинг с правами SYSTEM. По умолчанию в подпапках
        /// ProgramData пользователи могут создавать файлы.
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

        /// <summary>Переносит список служб и таймаут из папки ServiceWatchdog, если своих ещё нет.</summary>
        public static void MigrateLegacy()
        {
            if (!Directory.Exists(LegacyDir))
                return;
            EnsureDirectory();
            foreach (var target in new[] { ServicesPath, TimeoutPath })
            {
                string source = Path.Combine(LegacyDir, Path.GetFileName(target));
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
        }

        /// <summary>Отпечаток файлов настроек — по нему видно, что их изменили.</summary>
        public static string GetStamp()
        {
            return string.Join("|", new[] { ServicesPath, TimeoutPath, AllowedPath, DeniedPath, AppControlPath }
                .Select(p => Stamp(p).ToString()).ToArray());
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
            return ReadList(ServicesPath);
        }

        public static void SaveServices(IEnumerable<string> services)
        {
            WriteList(ServicesPath, services);
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

        public static AppControlSettings LoadAppControl()
        {
            var settings = new AppControlSettings { Allowed = ReadList(AllowedPath), Denied = ReadList(DeniedPath) };
            if (File.Exists(AppControlPath))
            {
                foreach (var line in File.ReadAllLines(AppControlPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    bool value = line.Substring(eq + 1).Trim() == "1";
                    if (key == "enabled")
                        settings.Enabled = value;
                    else if (key == "block")
                        settings.Block = value;
                    else if (key == "deny_enabled")
                        settings.DenyEnabled = value;
                    else if (key == "deny_block")
                        settings.DenyBlock = value;
                }
            }
            return settings;
        }

        public static void SaveAppControl(AppControlSettings settings)
        {
            WriteList(AllowedPath, settings.Allowed);
            WriteList(DeniedPath, settings.Denied);
            WriteText(AppControlPath,
                "enabled=" + (settings.Enabled ? "1" : "0") + Environment.NewLine
                + "block=" + (settings.Block ? "1" : "0") + Environment.NewLine
                + "deny_enabled=" + (settings.DenyEnabled ? "1" : "0") + Environment.NewLine
                + "deny_block=" + (settings.DenyBlock ? "1" : "0") + Environment.NewLine);
        }

        static List<string> ReadList(string path)
        {
            if (!File.Exists(path))
                return new List<string>();
            return File.ReadAllLines(path, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static void WriteList(string path, IEnumerable<string> items)
        {
            WriteText(path, string.Join(Environment.NewLine,
                items.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray()) + Environment.NewLine);
        }

        /// <summary>Запись через временный файл, чтобы мониторинг не прочитал файл наполовину.</summary>
        static void WriteText(string path, string text)
        {
            EnsureDirectory();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
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
