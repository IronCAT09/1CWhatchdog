using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    /// <summary>Значок в трее в сеансе пользователя. Сам службы не трогает — это делает фоновый мониторинг.</summary>
    sealed class TrayContext : ApplicationContext
    {
        readonly MainForm form;
        readonly NotifyIcon tray;
        readonly EventWaitHandle showEvent;
        readonly RegisteredWaitHandle showWait;

        public TrayContext()
        {
            form = new MainForm(false);
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

            form.Alert += (s, e) => tray.ShowBalloonTip(5000, "Монитор служб", e.Text, ToolTipIcon.Warning);

            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
                delegate { Post(ShowForm); }, null, Timeout.Infinite, false);

            if (!MonitorHost.IsRunning())
                tray.ShowBalloonTip(5000, "Монитор служб",
                    "Мониторинг не запущен. Двойной клик по значку — подробности.", ToolTipIcon.Warning);
        }

        void ShowForm()
        {
            form.ShowFromTray();
        }

        void Post(Action action)
        {
            try { form.BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        void ExitApp()
        {
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
