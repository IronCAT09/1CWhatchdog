using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace ServiceWatchdog
{
    /// <summary>
    /// Фоновый мониторинг. Запускается заданием Планировщика от имени SYSTEM при загрузке
    /// Windows и работает независимо от того, кто вошёл в систему. Настройки перечитывает
    /// при изменении файлов, события пишет в общий журнал — его показывает окно в трее.
    /// </summary>
    static class MonitorHost
    {
        const string MutexName = "Global\\ServiceWatchdog_Monitor";
        const int SettingsPollMs = 5000;

        public static bool IsRunning()
        {
            try
            {
                Mutex mutex;
                if (!Mutex.TryOpenExisting(MutexName, MutexRights.Synchronize, out mutex))
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

                try { Settings.EnsureDirectory(); }
                catch (Exception ex) { Log("Не удалось настроить права на папку настроек: " + ex.Message, true); }

                string stamp = Settings.GetStamp();
                var monitor = new ServiceMonitor(Settings.LoadServices(), Settings.LoadTimeout());
                monitor.Event += (s, e) => Log(e.Service + ": " + e.Message, e.Alert);
                Log("Мониторинг запущен, служб: " + monitor.Count + ", таймаут: " + monitor.TimeoutSeconds + " с", false);
                monitor.Start();

                while (true)
                {
                    Thread.Sleep(SettingsPollMs);
                    string current = Settings.GetStamp();
                    if (current == stamp)
                        continue;
                    try
                    {
                        Reload(monitor);
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

        static void Log(string text, bool alert)
        {
            Settings.AppendLog(new LogEntry(DateTime.Now, text, alert));
        }
    }
}
