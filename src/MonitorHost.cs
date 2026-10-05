using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace OneCWhatchdog
{
    /// <summary>
    /// Фоновый мониторинг. Запускается заданием Планировщика от имени SYSTEM при загрузке
    /// Windows и работает независимо от того, кто вошёл в систему. Настройки перечитывает
    /// при изменении файлов, события пишет в общий журнал — его показывает окно в трее.
    /// </summary>
    static class MonitorHost
    {
        const string MutexName = "Global\\1CWhatchdog_Monitor";
        /// <summary>Мьютекс мониторинга версий 1.1 и старше, когда программа называлась ServiceWatchdog.</summary>
        public const string LegacyMutexName = "Global\\ServiceWatchdog_Monitor";
        const int SettingsPollMs = 5000;

        public static bool IsRunning()
        {
            return IsRunning(MutexName);
        }

        public static bool IsRunning(string mutexName)
        {
            try
            {
                Mutex mutex;
                if (!Mutex.TryOpenExisting(mutexName, MutexRights.Synchronize, out mutex))
                    return false;
                mutex.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // мьютекс есть, просто нет прав на него
            }
        }

        public static void Run()
        {
            // Проверять наличие мьютекса из сеанса пользователя должен уметь любой.
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew, security))
            {
                if (!createdNew)
                    return;

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log("Аварийное завершение мониторинга: " + e.ExceptionObject, true);
                    Environment.Exit(1); // задание Планировщика перезапустит мониторинг
                };

                try
                {
                    Settings.EnsureDirectory();
                    Settings.MigrateLegacy();
                }
                catch (Exception ex)
                {
                    Log("Не удалось подготовить папку настроек: " + ex.Message, true);
                }

                string stamp = Settings.GetStamp();
                var monitor = new ServiceMonitor(Settings.LoadServices(), Settings.LoadTimeout());
                monitor.Event += (s, e) => Log(e.Service + ": " + e.Message, e.Alert);
                Log("Мониторинг запущен, служб: " + monitor.Count + ", таймаут: " + monitor.TimeoutSeconds + " с", false);
                monitor.Start();

                var appSettings = Settings.LoadAppControl();
                var appControl = new AppControl(appSettings);
                appControl.Event += (s, e) => Log(e.Message, e.Alert);
                Log("Контроль программ: " + Describe(appSettings), false);
                appControl.Start();

                while (true)
                {
                    Thread.Sleep(SettingsPollMs);
                    string current = Settings.GetStamp();
                    if (current == stamp)
                        continue;
                    try
                    {
                        Reload(monitor);
                        appSettings = ReloadAppControl(appControl, appSettings);
                        stamp = current;
                    }
                    catch (Exception ex)
                    {
                        // Файл мог быть занят в момент записи — попробуем на следующем круге.
                        Log("Не удалось перечитать настройки: " + ex.Message, false);
                    }
                }
            }
        }

        static void Reload(ServiceMonitor monitor)
        {
            var services = Settings.LoadServices();
            int timeout = Settings.LoadTimeout();

            var before = new HashSet<string>(monitor.GetWatched(), StringComparer.OrdinalIgnoreCase);
            var after = new HashSet<string>(services, StringComparer.OrdinalIgnoreCase);
            var added = after.Where(n => !before.Contains(n)).ToList();
            var removed = before.Where(n => !after.Contains(n)).ToList();
            int oldTimeout = monitor.TimeoutSeconds;

            monitor.ReplaceWatched(services);
            monitor.TimeoutSeconds = timeout;

            var parts = new List<string>();
            if (added.Count > 0)
                parts.Add("добавлены: " + string.Join(", ", added));
            if (removed.Count > 0)
                parts.Add("исключены: " + string.Join(", ", removed));
            if (monitor.TimeoutSeconds != oldTimeout)
                parts.Add("таймаут: " + monitor.TimeoutSeconds + " с");
            if (parts.Count > 0)
                Log("Настройки обновлены — " + string.Join("; ", parts), false);
        }

        static AppControlSettings ReloadAppControl(AppControl appControl, AppControlSettings before)
        {
            var after = Settings.LoadAppControl();
            appControl.Apply(after);

            var parts = new List<string>();
            if (after.Enabled != before.Enabled || after.Block != before.Block
                || after.DenyEnabled != before.DenyEnabled || after.DenyBlock != before.DenyBlock)
                parts.Add(Describe(after));
            DescribeListChanges(parts, before.Allowed, after.Allowed, "разрешены", "убраны из разрешённых");
            DescribeListChanges(parts, before.Denied, after.Denied, "запрещены", "убраны из запрещённых");
            if (parts.Count > 0)
                Log("Контроль программ — " + string.Join("; ", parts), false);
            return after;
        }

        static void DescribeListChanges(List<string> parts, List<string> before, List<string> after,
            string addedText, string removedText)
        {
            var oldList = new HashSet<string>(before.Select(AppControl.NormalizeName), StringComparer.OrdinalIgnoreCase);
            var newList = new HashSet<string>(after.Select(AppControl.NormalizeName), StringComparer.OrdinalIgnoreCase);
            var added = newList.Where(n => !oldList.Contains(n)).ToList();
            var removed = oldList.Where(n => !newList.Contains(n)).ToList();
            if (added.Count > 0)
                parts.Add(addedText + ": " + string.Join(", ", added));
            if (removed.Count > 0)
                parts.Add(removedText + ": " + string.Join(", ", removed));
        }

        static string Describe(AppControlSettings settings)
        {
            return "разрешённые программы " + DescribeMode(settings.Enabled, settings.Block, settings.Allowed.Count)
                + "; запрещённые программы " + DescribeMode(settings.DenyEnabled, settings.DenyBlock, settings.Denied.Count);
        }

        static string DescribeMode(bool enabled, bool block, int count)
        {
            if (!enabled)
                return "— выключено";
            return (block ? "— завершение" : "— только журнал") + ", в списке: " + count;
        }

        static void Log(string text, bool alert)
        {
            Settings.AppendLog(new LogEntry(DateTime.Now, text, alert));
        }
    }
}
