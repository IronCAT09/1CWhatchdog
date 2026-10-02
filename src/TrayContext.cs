using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    sealed class TrayContext : ApplicationContext
    {
        readonly ServiceMonitor monitor;
        readonly MainForm form;
        readonly NotifyIcon tray;
        readonly EventWaitHandle showEvent;
        readonly RegisteredWaitHandle showWait;

        public TrayContext()
        {
            monitor = new ServiceMonitor(Settings.LoadServices(), Settings.LoadTimeout());

            form = new MainForm(monitor);
            // Хэндл нужен сразу, чтобы BeginInvoke работал, пока окно скрыто.
            IntPtr handle = form.Handle;

            var menu = new ContextMenuStrip();
            var openItem = menu.Items.Add("Открыть", null, delegate { ShowForm(); });
            openItem.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitApp(); });

            tray = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "Монитор служб",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };

            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
                delegate { Post(ShowForm); }, null, Timeout.Infinite, false);

            monitor.Event += OnMonitorEvent;
            monitor.Start();

            form.AppendLog("Программа запущена, отслеживается служб: " + monitor.Count);
            if (monitor.Count == 0)
                tray.ShowBalloonTip(5000, "Монитор служб",
                    "Программа работает в трее. Двойной клик по значку — выбор служб.", ToolTipIcon.Info);
        }

        void ShowForm()
        {
            form.ShowFromTray();
        }

        void OnMonitorEvent(object sender, MonitorEventArgs e)
        {
            Post(delegate
            {
                form.AppendLog(e.Service + ": " + e.Message);
                if (e.Alert)
                    tray.ShowBalloonTip(5000, "Монитор служб", e.Service + ": " + e.Message, ToolTipIcon.Warning);
            });
        }

        void Post(Action action)
        {
            try { form.BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        void ExitApp()
        {
            monitor.Event -= OnMonitorEvent;
            monitor.Dispose();
            showWait.Unregister(null);
            showEvent.Dispose();

            tray.Visible = false;
            tray.Dispose();

            form.AllowClose = true;
            form.Close();
            form.Dispose();

            ExitThread();
        }
    }
}
