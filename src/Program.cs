using System;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace OneCWhatchdog
{
    static class Program
    {
        public const string AppName = "1CWhatchdog";
        public const string ShowEventName = "Local\\1CWhatchdog_Show";
        const string MutexName = "Local\\1CWhatchdog_SingleInstance";
        const string SettingsMutexName = "Local\\1CWhatchdog_SettingsInstance";
        const string SettingsEventName = "Local\\1CWhatchdog_ActivateSettings";

        /// <summary>
        /// Режимы запуска:
        ///   (без аргументов) — значок в трее и сразу открытое окно (запуск пользователем);
        ///   /tray            — только значок в трее (автозапуск при входе в Windows);
        ///   /monitor         — фоновый мониторинг (задание Планировщика от имени SYSTEM);
        ///   /settings        — окно настроек с правами администратора.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "/monitor")
            {
                MonitorHost.Run();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (mode == "/settings")
            {
                RunSettings();
                return;
            }

            bool trayOnly = mode == "/tray";
            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // Мьютекс создан экземпляром, запущенным с правами администратора.
                if (!trayOnly)
                    ShowRunningInstance();
                return;
            }

            using (mutex)
            {
                if (!createdNew)
                {
                    // Автозапуск при уже работающем значке ничего не показывает.
                    if (!trayOnly)
                        ShowRunningInstance();
                    return;
                }
                Application.Run(new TrayContext(!trayOnly));
            }
        }

        /// <summary>
        /// Окно настроек с правами администратора — одно на сеанс. Пока оно открыто,
        /// значок в трее вместо своего окна выводит на передний план это (через событие).
        /// </summary>
        static void RunSettings()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, SettingsMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    ActivateSettingsWindow();
                    return;
                }

                // Процесс без повышения прав (значок в трее) должен иметь право подать событие.
                var security = new EventWaitHandleSecurity();
                security.AddAccessRule(new EventWaitHandleAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
                security.AddAccessRule(new EventWaitHandleAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new EventWaitHandleAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));

                bool createdEvent;
                using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset,
                    SettingsEventName, out createdEvent, security))
                {
                    var form = new MainForm(true);
                    var wait = ThreadPool.RegisterWaitForSingleObject(activate, delegate
                    {
                        // InvalidOperationException покрывает и ObjectDisposedException.
                        try { form.BeginInvoke(new Action(form.ActivateWindow)); }
                        catch (InvalidOperationException) { }
                    }, null, Timeout.Infinite, false);

                    Application.Run(form);
                    wait.Unregister(null);
                }
            }
        }

        /// <summary>Если открыто окно настроек — выводит его на передний план и возвращает true.</summary>
        public static bool ActivateSettingsWindow()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(SettingsEventName,
                    EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
                {
                    // Разрешаем окну настроек перехватить фокус у нас.
                    AllowSetForegroundWindow(AsfwAny);
                    ev.Set();
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        const int AsfwAny = -1;

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>Уже запущен экземпляр в этом сеансе — просим его показать окно.</summary>
        static void ShowRunningInstance()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                    ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static bool IsElevated
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }
}
