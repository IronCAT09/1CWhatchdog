using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>
    /// Окно со списком служб и журналом. Без прав администратора — только просмотр
    /// и кнопка «Изменить настройки…», которая открывает это же окно с повышением прав.
    /// </summary>
    sealed class MainForm : Form
    {
        const int MaxLogLines = 500;
        const int InitialLogLines = 200;
        const int StatusColumn = 2;
        const int ErrorCancelled = 1223; // пользователь отказался в окне UAC

        readonly bool standalone;
        readonly bool canEdit;
        readonly TextBox filterBox = new TextBox();
        readonly CheckBox onlyWatchedBox = new CheckBox();
        readonly CheckBox autostartBox = new CheckBox();
        readonly Button elevateButton = new Button();
        readonly NumericUpDown timeoutBox = new NumericUpDown();
        readonly Timer timeoutCommitTimer = new Timer();
        readonly ListView list = new ListView();
        readonly ListBox logBox = new ListBox();
        readonly SplitContainer split = new SplitContainer();
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel monitorLabel = new ToolStripStatusLabel();
        readonly Timer refreshTimer = new Timer();
        readonly Timer logTimer = new Timer();
        readonly LogTail logTail = new LogTail(Settings.LogPath);

        HashSet<string> watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int timeoutSeconds = ServiceMonitor.DefaultTimeoutSeconds;
        string settingsStamp;
        List<ServiceInfo> services = new List<ServiceInfo>();
        bool updating;

        /// <summary>Новое событие журнала, о котором стоит показать уведомление.</summary>
        public event EventHandler<LogEntry> Alert;

        public bool AllowClose { get; set; }

        /// <param name="standalone">true — отдельное окно настроек (/settings), закрывается крестиком.</param>
        public MainForm(bool standalone)
        {
            this.standalone = standalone;
            canEdit = Program.IsElevated;
            AllowClose = standalone;

            Text = canEdit ? "Монитор служб" : "Монитор служб — просмотр";
            Icon = SystemIcons.Shield;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(960, 640);
            MinimumSize = new Size(660, 420);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(6, 6, 6, 2)
            };
            var searchLabel = new Label { Text = "Поиск:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) };
            filterBox.Width = 200;
            filterBox.TextChanged += delegate { FillList(); };

            onlyWatchedBox.Text = "Только отмеченные";
            onlyWatchedBox.AutoSize = true;
            onlyWatchedBox.Margin = new Padding(12, 5, 3, 3);
            onlyWatchedBox.CheckedChanged += delegate { FillList(); };

            var reloadButton = new Button { Text = "Обновить список", AutoSize = true };
            reloadButton.Click += delegate { ReloadServices(); };

            var timeoutLabel = new Label { Text = "Таймаут, с:", AutoSize = true, Margin = new Padding(12, 7, 0, 3) };
            timeoutBox.Minimum = ServiceMonitor.MinTimeoutSeconds;
            timeoutBox.Maximum = ServiceMonitor.MaxTimeoutSeconds;
            timeoutBox.Width = 70;
            timeoutBox.Enabled = canEdit;
            // Сохраняем с задержкой, чтобы не писать файл на каждое нажатие стрелки.
            timeoutCommitTimer.Interval = 800;
            timeoutCommitTimer.Tick += delegate { CommitTimeout(); };
            timeoutBox.ValueChanged += delegate
            {
                if (updating)
                    return;
                timeoutCommitTimer.Stop();
                timeoutCommitTimer.Start();
            };

            autostartBox.Text = "Автозапуск для всех пользователей";
            autostartBox.AutoSize = true;
            autostartBox.Margin = new Padding(12, 5, 3, 3);
            autostartBox.CheckedChanged += OnAutostartChanged;

            elevateButton.Text = "Изменить настройки…";
            elevateButton.AutoSize = true;
            elevateButton.Margin = new Padding(12, 3, 3, 3);
            elevateButton.Click += delegate { OpenElevatedSettings(); };

            top.Controls.AddRange(new Control[] { searchLabel, filterBox, onlyWatchedBox, reloadButton, timeoutLabel, timeoutBox });
            top.Controls.Add(canEdit ? (Control)autostartBox : elevateButton);

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
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(statusLabel);
            status.Items.Add(monitorLabel);

            // Порядок важен: Fill добавляется первым.
            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(status);

            refreshTimer.Interval = 2000;
            refreshTimer.Tick += delegate
            {
                ReloadSettingsIfChanged();
                RefreshStatuses();
                UpdateStatusBar();
            };

            LoadSettings();
            foreach (var entry in logTail.ReadInitial(InitialLogLines))
                AddLogEntry(entry);
            // Журнал читаем и при скрытом окне — ради уведомлений в трее.
            logTimer.Interval = 2000;
            logTimer.Tick += delegate { PollLog(); };
            logTimer.Start();
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
                LoadData();
            Activate();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            split.SplitterDistance = Math.Max(100, split.Height - 180);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (standalone)
                LoadData();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            refreshTimer.Enabled = Visible;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!standalone && WindowState == FormWindowState.Minimized)
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
            else if (timeoutCommitTimer.Enabled)
            {
                CommitTimeout();
            }
            base.OnFormClosing(e);
        }

        void LoadData()
        {
            ReloadSettingsIfChanged();
            ReloadServices();
            SyncAutostart();
            UpdateStatusBar();
        }

        void LoadSettings()
        {
            try
            {
                settingsStamp = Settings.GetStamp();
                watched = new HashSet<string>(Settings.LoadServices(), StringComparer.OrdinalIgnoreCase);
                timeoutSeconds = Math.Max(ServiceMonitor.MinTimeoutSeconds,
                    Math.Min(ServiceMonitor.MaxTimeoutSeconds, Settings.LoadTimeout()));
            }
            catch (Exception)
            {
                // Файл мог быть занят в момент записи — перечитаем на следующем тике.
                settingsStamp = null;
            }

            updating = true;
            try { timeoutBox.Value = timeoutSeconds; }
            finally { updating = false; }
        }

        void ReloadSettingsIfChanged()
        {
            if (Settings.GetStamp() == settingsStamp)
                return;
            LoadSettings();
            FillList();
        }

        void SaveSettings(Action save)
        {
            try
            {
                save();
                settingsStamp = Settings.GetStamp();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить настройки:\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadSettings();
                FillList();
            }
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
            if (watched.Contains(name) == e.Item.Checked)
                return;

            if (!canEdit)
            {
                // Только просмотр: возвращаем отметку как было.
                BeginInvoke(new Action(FillList));
                return;
            }

            if (e.Item.Checked)
                watched.Add(name);
            else
                watched.Remove(name);
            SaveSettings(delegate { Settings.SaveServices(watched); });

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
            if (!canEdit || seconds == timeoutSeconds)
                return;

            timeoutSeconds = seconds;
            SaveSettings(delegate { Settings.SaveTimeout(seconds); });
            UpdateStatusBar();
        }

        void OnAutostartChanged(object sender, EventArgs e)
        {
            if (updating)
                return;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (autostartBox.Checked)
                {
                    string exe = Autostart.Install();
                    MessageBox.Show(this,
                        "Программа установлена: " + exe + "\n\n"
                        + "Мониторинг запущен и будет стартовать при загрузке Windows — "
                        + "независимо от того, кто вошёл в систему.\n"
                        + "Значок в трее появится при входе любого пользователя.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Autostart.Uninstall();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось изменить автозапуск:\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                SyncAutostart();
                UpdateStatusBar();
            }
        }

        void SyncAutostart()
        {
            if (!canEdit)
                return;
            updating = true;
            try { autostartBox.Checked = Autostart.IsEnabled(); }
            finally { updating = false; }
        }

        void OpenElevatedSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/settings")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != ErrorCancelled)
                    MessageBox.Show(this, "Не удалось открыть настройки:\n" + ex.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void PollLog()
        {
            foreach (var entry in logTail.ReadNew())
            {
                AddLogEntry(entry);
                if (entry.Alert)
                {
                    var handler = Alert;
                    if (handler != null)
                        handler(this, entry);
                }
            }
        }

        void AddLogEntry(LogEntry entry)
        {
            logBox.Items.Add(entry.ToString());
            while (logBox.Items.Count > MaxLogLines)
                logBox.Items.RemoveAt(0);
            logBox.TopIndex = logBox.Items.Count - 1;
        }

        void UpdateStatusBar()
        {
            statusLabel.Text = "Отслеживается служб: " + watched.Count
                + "   •   перезапуск, если служба не работает " + timeoutSeconds + " с";

            bool running = MonitorHost.IsRunning();
            if (running)
                monitorLabel.Text = "Мониторинг работает";
            else if (canEdit)
                monitorLabel.Text = "Мониторинг не запущен — включите «Автозапуск для всех пользователей»";
            else
                monitorLabel.Text = "Мониторинг не запущен — нужна настройка администратором";
            monitorLabel.ForeColor = running ? Color.DarkGreen : Color.Firebrick;
        }

        static bool Contains(string text, string part)
        {
            return text.IndexOf(part, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }
}
