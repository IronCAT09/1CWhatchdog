using System;
using System.Threading;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    static class Program
    {
        public const string AppName = "ServiceWatchdog";
        public const string ShowEventName = "Local\\ServiceWatchdog_Show";
        const string MutexName = "Local\\ServiceWatchdog_SingleInstance";

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Уже запущен экземпляр — просим его показать окно и выходим.
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                            ev.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
                GC.KeepAlive(mutex);
            }
        }
    }
}
