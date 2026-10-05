using System;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    static class Program
    {
        public const string AppName = "ServiceWatchdog";
        public const string ShowEventName = "Local\\ServiceWatchdog_Show";
        const string MutexName = "Local\\ServiceWatchdog_SingleInstance";

        /// <summary>
        /// Режимы запуска:
        ///   (без аргументов) — значок в трее в сеансе пользователя;
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
                Application.Run(new MainForm(true));
                return;
            }

            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // Мьютекс создан экземпляром, запущенным с правами администратора.
                ShowRunningInstance();
                return;
            }

            using (mutex)
            {
                if (!createdNew)
                {
                    ShowRunningInstance();
                    return;
                }
                Application.Run(new TrayContext());
            }
        }

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
