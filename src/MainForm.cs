using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.ServiceProcess;
using System.Windows.Forms;

namespace ServiceWatchdog
{
    sealed class ServiceInfo
    {
        public string Name;
        public string DisplayName;
        public ServiceStartMode? StartType;
    }

    sealed class MainForm : Form
    {
        const int MaxLogLines = 500;
        const int StatusColumn = 2;

        readonly ServiceMonitor monitor;
        readonly TextBox filterBox = new TextBox();
        readonly CheckBox onlyWatchedBox = new CheckBox();
        readonly CheckBox autostartBox = new CheckBox();
        readonly NumericUpDown timeoutBox = new NumericUpDown();
        readonly Timer timeoutCommitTimer = new Timer();
        readonly ListView list = new ListView();
        readonly ListBox logBox = new ListBox();
        readonly SplitContainer split = new SplitContainer();
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        readonly Timer refreshTimer = new Timer();

        List<ServiceInfo> services = new List<ServiceInfo>();
        bool updating;

        public bool AllowClose { get; set; }

        public MainForm(ServiceMonitor monitor)
        {
            this.monitor = monitor;

            Text = "Монитор служб";
            Icon = SystemIcons.Shield;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(880, 640);
            MinimumSize = new Size(620, 420);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(6, 6, 6, 2)
            };
            var searchLabel = new Label { Text = "Поиск:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) };
            filterBox.Width = 220;
            filterBox.TextChanged += delegate { FillList(); };

            onlyWatchedBox.Text = "Только отмеченные";
            onlyWatchedBox.AutoSize = true;
            onlyWatchedBox.Margin = new Padding(12, 5, 3, 3);
            onlyWatchedBox.CheckedChanged += delegate { FillList(); };

            var reloadButton = new Button { Text = "Обновить список", AutoSize = true };
            reloadButton.Click += delegate { ReloadServices(); };

            autostartBox.Text = "Запускать при входе в Windows";
            autostartBox.AutoSize = true;
            autostartBox.Margin = new Padding(12, 5, 3, 3);
            autostartBox.CheckedChanged += OnAutostartChanged;

            var timeoutLabel = new Label { Text = "Таймаут, с:", AutoSize = true, Margin = new Padding(12, 7, 0, 3) };
            timeoutBox.Minimum = ServiceMonitor.MinTimeoutSeconds;
            timeoutBox.Maximum = ServiceMonitor.MaxTimeoutSeconds;
            timeoutBox.Value = monitor.TimeoutSeconds;
            timeoutBox.Width = 70;
            // Применяем с задержкой, чтобы не писать в журнал каждое нажатие стрелки.
            timeoutCommitTimer.Interval = 800;
            timeoutCommitTimer.Tick += delegate { CommitTimeout(); };
            timeoutBox.ValueChanged += delegate { timeoutCommitTimer.Stop(); timeoutCommitTimer.Start(); };

            top.Controls.AddRange(new Control[] { searchLabel, filterBox, onlyWatchedBox, reloadButton, timeoutLabel, timeoutBox, autostartBox });

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.Columns.Add("Служба", 340);
            list.Columns.Add("Имя", 180);
            list.Columns.Add("Состояние", 130);
            list.Columns.Add("Тип запуска", 130);
            list.ItemChecked += OnItemChecked;

            logBox.Dock = DockStyle.Fill;
            logBox.IntegralHeight = false;
            logBox.HorizontalScrollbar = true;
            var logGroup = new GroupBox { Text = "Журнал", Dock = DockStyle.Fill, Padding = new Padding(6) };
            logGroup.Controls.Add(logBox);

            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.FixedPanel = FixedPanel.Panel2;
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(logGroup);

            var status = new StatusStrip();
            status.Items.Add(statusLabel);

            // Порядок важен: Fill добавляется первым.
            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(status);

            refreshTimer.Interval = 2000;
            refreshTimer.Tick += delegate { RefreshStatuses(); };
        }

        public void ShowFromTray()
        {
            bool wasHidden = !Visible;
            // Сначала показываем окно: ListView создаёт свой хэндл только при показе
            // и в этот момент заново генерирует ItemChecked для всех строк.
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            if (wasHidden)
            {
                ReloadServices();
                SyncAutostart();
            }
            Activate();
        }

        public void AppendLog(string message)
        {
            DateTime now = DateTime.Now;
            Settings.AppendLog(now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message);

            logBox.Items.Add(now.ToString("dd.MM HH:mm:ss") + "  " + message);
            while (logBox.Items.Count > MaxLogLines)
                logBox.Items.RemoveAt(0);
            logBox.TopIndex = logBox.Items.Count - 1;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            split.SplitterDistance = Math.Max(100, split.Height - 180);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            refreshTimer.Enabled = Visible;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
                Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик только прячет окно в трей; выход — через меню значка в трее.
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        void ReloadServices()
        {
            var result = new List<ServiceInfo>();
            foreach (var sc in ServiceController.GetServices())
            {
                using (sc)
                {
                    try
                    {
                        result.Add(new ServiceInfo
                        {
                            Name = sc.ServiceName,
                            DisplayName = sc.DisplayName,
                            StartType = sc.StartType
                        });
                    }
                    catch (Exception) { } // служба удалена во время перечисления
                }
            }
            services = result;
            FillList();
        }

        void FillList()
        {
            var watched = new HashSet<string>(monitor.GetWatched(), StringComparer.OrdinalIgnoreCase);
            var rows = new List<ServiceInfo>(services);
            var known = new HashSet<string>(services.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var name in watched)
                if (!known.Contains(name))
                    rows.Add(new ServiceInfo { Name = name, DisplayName = name });

            string filter = filterBox.Text.Trim();
            var visible = rows
                .Where(s => !onlyWatchedBox.Checked || watched.Contains(s.Name))
                .Where(s => filter.Length == 0 || Contains(s.DisplayName, filter) || Contains(s.Name, filter))
                .OrderByDescending(s => watched.Contains(s.Name))
                .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase);

            updating = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var s in visible)
                {
                    var item = new ListViewItem(s.DisplayName) { Tag = s.Name, Checked = watched.Contains(s.Name) };
                    item.SubItems.Add(s.Name);
                    item.SubItems.Add("");
                    item.SubItems.Add(Texts.StartType(s.StartType));
                    list.Items.Add(item);
                }
            }
            finally
            {
                list.EndUpdate();
                updating = false;
            }
            RefreshStatuses();
            UpdateStatusBar();
        }

        void RefreshStatuses()
        {
            var statuses = new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var sc in ServiceController.GetServices())
            {
                using (sc)
                {
                    try { statuses[sc.ServiceName] = sc.Status; }
                    catch (Exception) { }
                }
            }

            foreach (ListViewItem item in list.Items)
            {
                var name = item == null ? null : item.Tag as string;
                if (name == null || item.SubItems.Count <= StatusColumn)
                    continue;

                ServiceControllerStatus st;
                ServiceControllerStatus? status = statuses.TryGetValue(name, out st)
                    ? st : (ServiceControllerStatus?)null;

                string text = Texts.Status(status);
                if (item.SubItems[StatusColumn].Text != text)
                    item.SubItems[StatusColumn].Text = text;

                Color color = item.Checked && status != ServiceControllerStatus.Running
                    ? Color.Firebrick : SystemColors.WindowText;
                if (item.ForeColor != color)
                    item.ForeColor = color;
            }
        }

        void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (updating || !list.Created)
                return;

            string name = e.Item.Tag as string;
            if (name == null)
                return;
            // Служебные события ListView приходят и без действия пользователя — игнорируем «без изменений».
            if (monitor.GetWatched().Contains(name, StringComparer.OrdinalIgnoreCase) == e.Item.Checked)
                return;

            monitor.SetWatched(name, e.Item.Checked);
            try
            {
                Settings.SaveServices(monitor.GetWatched());
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось сохранить настройки: " + ex.Message);
            }
            AppendLog((e.Item.Checked ? "Добавлена в мониторинг: " : "Исключена из мониторинга: ") + e.Item.Text);
            // Перерисовку откладываем: внутри обработчика уведомления ListView перебирать Items небезопасно.
            BeginInvoke(new Action(delegate
            {
                RefreshStatuses();
                UpdateStatusBar();
            }));
        }

        void CommitTimeout()
        {
            timeoutCommitTimer.Stop();
            int seconds = (int)timeoutBox.Value;
            if (seconds == monitor.TimeoutSeconds)
                return;

            monitor.TimeoutSeconds = seconds;
            try
            {
                Settings.SaveTimeout(seconds);
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось сохранить таймаут: " + ex.Message);
            }
            AppendLog("Таймаут изменён: " + seconds + " с");
            UpdateStatusBar();
        }

        void OnAutostartChanged(object sender, EventArgs e)
        {
            if (updating)
                return;
            try
            {
                if (autostartBox.Checked)
                    Autostart.Enable();
                else
                    Autostart.Disable();
                AppendLog(autostartBox.Checked ? "Автозапуск включён" : "Автозапуск отключён");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось изменить автозапуск:\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SyncAutostart();
            }
        }

        void SyncAutostart()
        {
            updating = true;
            try { autostartBox.Checked = Autostart.IsEnabled(); }
            finally { updating = false; }
        }

        void UpdateStatusBar()
        {
            statusLabel.Text = "Отслеживается служб: " + monitor.Count
                + "   •   перезапуск, если служба не работает "
                + monitor.TimeoutSeconds + " с";
        }

        static bool Contains(string text, string part)
        {
            return text.IndexOf(part, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }
}
