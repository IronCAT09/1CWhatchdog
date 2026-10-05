using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OneCWhatchdog
{
    /// <summary>Все настройки программы — содержимое settings.json.</summary>
    [DataContract]
    sealed class AppSettings
    {
        [DataMember(Name = "services", Order = 1)]
        public ServicesSettings Services;

        [DataMember(Name = "allowedPrograms", Order = 2)]
        public ProgramListSettings AllowedPrograms;

        [DataMember(Name = "deniedPrograms", Order = 3)]
        public ProgramListSettings DeniedPrograms;

        /// <summary>Всплывающие уведомления в трее; нет в файле — показывать.</summary>
        [DataMember(Name = "notifications", Order = 4)]
        public bool? Notifications;

        public bool NotificationsEnabled
        {
            get { return Notifications != false; }
        }

        /// <summary>Заполняет отсутствующие разделы значениями по умолчанию.</summary>
        public AppSettings Normalize()
        {
            if (Services == null)
                Services = new ServicesSettings();
            Services.Normalize();
            if (AllowedPrograms == null)
                AllowedPrograms = new ProgramListSettings();
            AllowedPrograms.Normalize();
            if (DeniedPrograms == null)
                DeniedPrograms = new ProgramListSettings();
            DeniedPrograms.Normalize();
            if (Notifications == null)
                Notifications = true;
            return this;
        }

        public AppControlSettings ToAppControl()
        {
            return new AppControlSettings
            {
                Enabled = AllowedPrograms.Enabled,
                Block = AllowedPrograms.Block,
                Allowed = new List<string>(AllowedPrograms.Programs),
                DenyEnabled = DeniedPrograms.Enabled,
                DenyBlock = DeniedPrograms.Block,
                Denied = new List<string>(DeniedPrograms.Programs)
            };
        }

        public void SetAppControl(AppControlSettings appControl)
        {
            AllowedPrograms = new ProgramListSettings
            {
                Enabled = appControl.Enabled,
                Block = appControl.Block,
                Programs = new List<string>(appControl.Allowed)
            };
            DeniedPrograms = new ProgramListSettings
            {
                Enabled = appControl.DenyEnabled,
                Block = appControl.DenyBlock,
                Programs = new List<string>(appControl.Denied)
            };
            Normalize();
        }

        internal static List<string> CleanList(IEnumerable<string> items)
        {
            return (items ?? Enumerable.Empty<string>())
                .Where(s => s != null)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    [DataContract]
    sealed class ServicesSettings
    {
        /// <summary>Имена отслеживаемых служб.</summary>
        [DataMember(Name = "watched", Order = 1)]
        public List<string> Watched;

        [DataMember(Name = "timeoutSeconds", Order = 2)]
        public int TimeoutSeconds;

        public void Normalize()
        {
            Watched = AppSettings.CleanList(Watched);
            if (TimeoutSeconds <= 0)
                TimeoutSeconds = ServiceMonitor.DefaultTimeoutSeconds;
            TimeoutSeconds = Math.Max(ServiceMonitor.MinTimeoutSeconds,
                Math.Min(ServiceMonitor.MaxTimeoutSeconds, TimeoutSeconds));
        }
    }

    [DataContract]
    sealed class ProgramListSettings
    {
        [DataMember(Name = "enabled", Order = 1)]
        public bool Enabled;

        /// <summary>true — завершать программы, false — только записывать в журнал.</summary>
        [DataMember(Name = "block", Order = 2)]
        public bool Block;

        /// <summary>Имена exe-файлов.</summary>
        [DataMember(Name = "programs", Order = 3)]
        public List<string> Programs;

        public void Normalize()
        {
            Programs = AppSettings.CleanList(Programs);
        }
    }

    /// <summary>
    /// Настройки (settings.json) и журнал в C:\ProgramData\1CWhatchdog. Изменять их могут только
    /// администраторы и SYSTEM, остальные пользователи — только читать.
    /// </summary>
    static class Settings
    {
        const long MaxLogSize = 1024 * 1024;

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Program.AppName);
        public static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
        public static readonly string LogPath = Path.Combine(Dir, "watchdog.log");
        /// <summary>Папка настроек версий до переименования (ServiceWatchdog 1.0–1.1).</summary>
        static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServiceWatchdog");
        /// <summary>Отдельные текстовые файлы, в которых настройки хранились до 1.3.</summary>
        static readonly string[] TextFiles =
            { "services.txt", "timeout.txt", "allowed.txt", "denied.txt", "appcontrol.txt", "notifications.txt" };
        static readonly object logSync = new object();

        /// <summary>
        /// Создаёт папку и закрывает её на запись для обычных пользователей: списки служб и
        /// программ исполняет мониторинг с правами SYSTEM. По умолчанию в подпапках
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

        /// <summary>
        /// Если settings.json ещё нет — собирает его из текстовых файлов прежних версий
        /// (1CWhatchdog 1.2 или ServiceWatchdog 1.x) и удаляет текстовые файлы 1.2.
        /// </summary>
        public static void MigrateLegacy()
        {
            if (File.Exists(SettingsPath))
                return;
            var legacy = LoadTextFiles(Dir) ?? LoadTextFiles(LegacyDir);
            if (legacy == null)
                return;
            Save(legacy.Normalize());
            foreach (var name in TextFiles)
            {
                try { File.Delete(Path.Combine(Dir, name)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Отпечаток файла настроек — по нему видно, что его изменили.</summary>
        public static string GetStamp()
        {
            try
            {
                var info = new FileInfo(SettingsPath);
                return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        /// <summary>
        /// Читает настройки. Если settings.json ещё не создан — берёт их из файлов прежних
        /// версий или значения по умолчанию. Повреждённый файл — InvalidDataException.
        /// </summary>
        public static AppSettings Load()
        {
            if (!File.Exists(SettingsPath))
                return (LoadTextFiles(Dir) ?? LoadTextFiles(LegacyDir) ?? new AppSettings()).Normalize();

            try
            {
                using (var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length == 0)
                        return new AppSettings().Normalize();
                    var settings = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream);
                    return (settings ?? new AppSettings()).Normalize();
                }
            }
            catch (SerializationException ex)
            {
                throw new InvalidDataException("Файл настроек повреждён: " + SettingsPath + "\n" + ex.Message, ex);
            }
        }

        /// <summary>Читает настройки, меняет и записывает обратно (нужны права администратора).</summary>
        public static void Update(Action<AppSettings> change)
        {
            var settings = Load();
            change(settings);
            Save(settings.Normalize());
        }

        /// <summary>Показывать ли уведомления в трее; при ошибке чтения — показывать.</summary>
        public static bool LoadNotifications()
        {
            try
            {
                return Load().NotificationsEnabled;
            }
            catch (Exception)
            {
                return true;
            }
        }

        static void Save(AppSettings settings)
        {
            string json;
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(writer, settings);
                    writer.Flush();
                }
                json = Encoding.UTF8.GetString(ms.ToArray());
            }
            WriteText(SettingsPath, json + Environment.NewLine);
        }

        /// <summary>Запись через временный файл, чтобы мониторинг не прочитал файл наполовину.</summary>
        static void WriteText(string path, string text)
        {
            EnsureDirectory();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }

        /// <summary>Настройки из текстовых файлов прежних версий; null — если их нет.</summary>
        static AppSettings LoadTextFiles(string dir)
        {
            if (!TextFiles.Any(name => File.Exists(Path.Combine(dir, name))))
                return null;

            var settings = new AppSettings
            {
                Services = new ServicesSettings { Watched = ReadList(Path.Combine(dir, "services.txt")) },
                AllowedPrograms = new ProgramListSettings { Programs = ReadList(Path.Combine(dir, "allowed.txt")) },
                DeniedPrograms = new ProgramListSettings { Programs = ReadList(Path.Combine(dir, "denied.txt")) }
            };

            int timeout;
            string timeoutPath = Path.Combine(dir, "timeout.txt");
            if (File.Exists(timeoutPath) && int.TryParse(File.ReadAllText(timeoutPath).Trim(), out timeout))
                settings.Services.TimeoutSeconds = timeout;

            string appControlPath = Path.Combine(dir, "appcontrol.txt");
            if (File.Exists(appControlPath))
            {
                foreach (var line in File.ReadAllLines(appControlPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    bool value = line.Substring(eq + 1).Trim() == "1";
                    if (key == "enabled")
                        settings.AllowedPrograms.Enabled = value;
                    else if (key == "block")
                        settings.AllowedPrograms.Block = value;
                    else if (key == "deny_enabled")
                        settings.DeniedPrograms.Enabled = value;
                    else if (key == "deny_block")
                        settings.DeniedPrograms.Block = value;
                }
            }

            string notificationsPath = Path.Combine(dir, "notifications.txt");
            if (File.Exists(notificationsPath))
                settings.Notifications = File.ReadAllText(notificationsPath).Trim() != "0";
            return settings;
        }

        static List<string> ReadList(string path)
        {
            return File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
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
