using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Windows.Forms;

namespace OneCWhatchdog
{
    sealed class ServiceInfo
    {
        public string Name;
        public string DisplayName;
        public ServiceStartMode? StartType;
    }

    /// <summary>
    /// Окно с вкладками «Службы» и «Разрешённые программы» и общим журналом. Без прав
    /// администратора — только просмотр и кнопка «Изменить настройки…», которая открывает
    /// это же окно с повышением прав.
    /// </summary>
    sealed class MainForm : Form
    {
        const int MaxLogLines = 500;
        const int InitialLogLines = 200;
        const int StatusColumn = 2;
        const int ErrorCancelled = 1223; // пользователь отказался в окне UAC

        readonly bool standalone;
        readonly bool canEdit;

        // Общее
        readonly CheckBox autostartBox = new CheckBox();
        readonly Button elevateButton = new Button();
        readonly Button defenderButton = new Button();
        bool defenderExcluded;
        readonly ListBox logBox = new ListBox();
        readonly SplitContainer split = new SplitContainer();
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel monitorLabel = new ToolStripStatusLabel();
        readonly Timer refreshTimer = new Timer();
        readonly Timer logTimer = new Timer();
        readonly LogTail logTail = new LogTail(Settings.LogPath);

        // Вкладка «Службы»
        readonly TextBox filterBox = new TextBox();
        readonly CheckBox onlyWatchedBox = new CheckBox();
        readonly NumericUpDown timeoutBox = new NumericUpDown();
        readonly Timer timeoutCommitTimer = new Timer();
        readonly ListView list = new ListView();

        // Вкладки «Разрешённые программы» и «Запрещённые программы»
        readonly ProgramListPanel allowPanel;
        readonly ProgramListPanel denyPanel;

        HashSet<string> watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int timeoutSeconds = ServiceMonitor.DefaultTimeoutSeconds;
        AppControlSettings appSettings = new AppControlSettings();
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

            Text = canEdit ? "1CWhatchdog" : "1CWhatchdog — просмотр";
            Icon = AppResources.WindowIcon;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(960, 680);
            MinimumSize = new Size(700, 460);

            allowPanel = new ProgramListPanel(
                "Контролировать запуск программ",
                "Завершать программы не из списка (иначе — только запись в журнал)",
                "Ограничение действует на всех пользователей, включая администраторов. "
                + "Программы сравниваются по имени exe-файла. Всегда разрешены программы из папки Windows, "
                + "Защитник Windows, WebView2 (нужен самой Windows) и сам 1CWhatchdog.",
                canEdit, false);
            allowPanel.ConfirmBlocking = ConfirmAllowBlocking;
            allowPanel.Changed += delegate { SaveAppSettings(); };

            denyPanel = new ProgramListPanel(
                "Запрещать программы из списка",
                "Завершать запрещённые программы (иначе — только запись в журнал)",
                "Ограничение действует на всех пользователей, включая администраторов, и на программы "
                + "из папки Windows (cmd.exe, regedit.exe…). Запрет важнее списка разрешённых. "
                + "Программы сравниваются по имени exe-файла.",
                canEdit, true);
            denyPanel.Changed += delegate { SaveAppSettings(); };

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(CreateServicesTab());
            tabs.TabPages.Add(CreatePanelTab("Разрешённые программы", allowPanel));
            tabs.TabPages.Add(CreatePanelTab("Запрещённые программы", denyPanel));
            tabs.TabPages.Add(CreateAboutTab());

            logBox.Dock = DockStyle.Fill;
            logBox.IntegralHeight = false;
            logBox.HorizontalScrollbar = true;
            var logGroup = new GroupBox { Text = "Журнал", Dock = DockStyle.Fill, Padding = new Padding(6) };
            logGroup.Controls.Add(logBox);

            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.FixedPanel = FixedPanel.Panel2;
            split.Panel1.Controls.Add(tabs);
            split.Panel2.Controls.Add(logGroup);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2) };
            autostartBox.Text = "Автозапуск для всех пользователей";
            autostartBox.AutoSize = true;
            autostartBox.Margin = new Padding(3, 5, 3, 3);
            autostartBox.CheckedChanged += OnAutostartChanged;
            elevateButton.Text = "Изменить настройки…";
            elevateButton.AutoSize = true;
            elevateButton.Click += delegate { OpenElevatedSettings(); };
            top.Controls.Add(canEdit ? (Control)autostartBox : elevateButton);
            if (canEdit)
            {
                defenderButton.Text = "Добавить в исключения Защитника";
                defenderButton.AutoSize = true;
                defenderButton.Margin = new Padding(16, 1, 3, 3);
                defenderButton.Click += delegate { ToggleDefenderExclusion(); };
                top.Controls.Add(defenderButton);
            }
            if (!canEdit)
                top.Controls.Add(new Label
                {
                    Text = "Режим просмотра: менять настройки может только администратор.",
                    AutoSize = true,
                    Margin = new Padding(12, 8, 3, 3)
                });

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

        TabPage CreateServicesTab()
        {
            var page = new TabPage("Службы");

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3, 4, 3, 2) };
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

            top.Controls.AddRange(new Control[] { searchLabel, filterBox, onlyWatchedBox, reloadButton, timeoutLabel, timeoutBox });

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

            page.Controls.Add(list);
            page.Controls.Add(top);
            return page;
        }

        const string ProjectUrl = "https://github.com/IronCAT09/1CWhatchdog";

        static TabPage CreateAboutTab()
        {
            var page = new TabPage("О программе") { AutoScroll = true };

            var logo = new PictureBox
            {
                Image = AppResources.Logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(160, 160),
                Margin = new Padding(16)
            };

            var info = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 16, 16, 16)
            };

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var title = new Label { Text = "1CWhatchdog", AutoSize = true };
            title.Font = new Font(title.Font.FontFamily, 18f, FontStyle.Bold);
            info.Controls.Add(title);
            info.Controls.Add(new Label
            {
                Text = "Версия " + version.ToString(3),
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 0, 3, 12)
            });
            info.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Margin = new Padding(3, 0, 3, 12),
                Text = "Следит за службами Windows и перезапускает их, если служба перестала работать. "
                     + "Контролирует запуск программ пользователями: список разрешённых и список запрещённых.\n\n"
                     + "Мониторинг работает в фоне от имени SYSTEM и не зависит от того, кто вошёл в систему. "
                     + "Значок в трее показывает состояние и уведомления; менять настройки может только администратор."
            });

            info.Controls.Add(CreateLink("Проект на GitHub: ", ProjectUrl, ProjectUrl));
            info.Controls.Add(CreateLink("Настройки и журнал: ", Settings.Dir, Settings.Dir));
            info.Controls.Add(new Label
            {
                Text = "Программа: " + Application.ExecutablePath,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 6, 3, 3)
            });

            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
            layout.Controls.Add(logo);
            layout.Controls.Add(info);
            page.Controls.Add(layout);
            return page;
        }

        /// <summary>Подпись со ссылкой: открывает адрес в браузере или папку в Проводнике.</summary>
        static Control CreateLink(string caption, string text, string target)
        {
            var link = new LinkLabel { Text = caption + text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
            link.LinkArea = new LinkArea(caption.Length, text.Length);
            link.LinkClicked += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(link.FindForm(), "Не удалось открыть:\n" + target + "\n\n" + ex.Message,
                        "1CWhatchdog", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            return link;
        }

        static TabPage CreatePanelTab(string title, Control panel)
        {
            var page = new TabPage(title);
            page.Controls.Add(panel);
            return page;
        }

        public void ShowFromTray()
        {
            // Открыто окно настроек с правами администратора — показываем его, а не второе окно.
            if (!standalone && Program.ActivateSettingsWindow())
            {
                Hide();
                return;
            }

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

        /// <summary>Вывести окно настроек на передний план (по сигналу от значка в трее).</summary>
        public void ActivateWindow()
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Show();
            // Без этого Windows может лишь мигнуть кнопкой на панели задач.
            TopMost = true;
            TopMost = false;
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
            SyncDefender();
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
                appSettings = Settings.LoadAppControl();
            }
            catch (Exception)
            {
                // Файл мог быть занят в момент записи — перечитаем на следующем тике.
                settingsStamp = null;
            }

            updating = true;
            try
            {
                timeoutBox.Value = timeoutSeconds;
                allowPanel.SetData(appSettings.Enabled, appSettings.Block, appSettings.Allowed);
                denyPanel.SetData(appSettings.DenyEnabled, appSettings.DenyBlock, appSettings.Denied);
            }
            finally
            {
                updating = false;
            }
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

        // ---------- Службы ----------

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

        // ---------- Разрешённые и запрещённые программы ----------

        bool ConfirmAllowBlocking()
        {
            int count = allowPanel.Names.Count;
            string list = count == 0
                ? "Список разрешённых программ пуст!"
                : "Разрешено программ: " + count + ".";
            return MessageBox.Show(this,
                "Все программы не из списка будут сразу завершаться у всех пользователей, включая администраторов "
                + "(кроме программ из папки Windows).\n\n" + list + "\n\n"
                + "Убедитесь, что в списке есть всё нужное для работы (например, 1cv8.exe, 1cv8c.exe). "
                + "Можно сначала поработать в режиме «только запись в журнал» и посмотреть, что запускается.\n\n"
                + "Включить завершение программ?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        void SaveAppSettings()
        {
            appSettings = new AppControlSettings
            {
                Enabled = allowPanel.ListEnabled,
                Block = allowPanel.Block,
                Allowed = allowPanel.Names,
                DenyEnabled = denyPanel.ListEnabled,
                DenyBlock = denyPanel.Block,
                Denied = denyPanel.Names
            };
            var snapshot = appSettings;
            SaveSettings(delegate { Settings.SaveAppControl(snapshot); });
            UpdateStatusBar();
        }

        // ---------- Автозапуск и права ----------

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

        void SyncDefender()
        {
            if (!canEdit)
                return;
            try
            {
                defenderExcluded = DefenderExclusion.IsExcluded();
                defenderButton.Text = defenderExcluded
                    ? "Убрать из исключений Защитника"
                    : "Добавить в исключения Защитника";
                defenderButton.Enabled = true;
            }
            catch (InvalidOperationException ex)
            {
                // Например, Защитник отключён сторонним антивирусом — кнопка не нужна.
                defenderButton.Text = "Защитник Windows недоступен";
                defenderButton.Enabled = false;
                new ToolTip().SetToolTip(defenderButton, ex.Message);
            }
        }

        void ToggleDefenderExclusion()
        {
            string path = DefenderExclusion.ExcludedPath;
            if (!defenderExcluded && MessageBox.Show(this,
                    "Папка " + path + " будет добавлена в исключения Защитника Windows: "
                    + "он перестанет проверять и блокировать файлы в ней.\n\n"
                    + "Писать в эту папку могут только администраторы, поэтому исключение безопасно.\n\n"
                    + "Добавить?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Cursor = Cursors.WaitCursor;
            try
            {
                if (defenderExcluded)
                    DefenderExclusion.Remove();
                else
                    DefenderExclusion.Add();
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, "Не удалось изменить исключения Защитника:\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                SyncDefender();
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
            if (Program.ActivateSettingsWindow())
            {
                Hide();
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/settings")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                // Окно настроек заменяет окно просмотра; само приложение остаётся в трее.
                Hide();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != ErrorCancelled)
                    MessageBox.Show(this, "Не удалось открыть настройки:\n" + ex.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- Журнал и строка состояния ----------

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
            statusLabel.Text = "Служб: " + watched.Count + ", таймаут " + timeoutSeconds + " с"
                + "   •   разрешённые: " + DescribeMode(appSettings.Enabled, appSettings.Block)
                + "   •   запрещённые: " + DescribeMode(appSettings.DenyEnabled, appSettings.DenyBlock);

            bool running = MonitorHost.IsRunning();
            if (running)
                monitorLabel.Text = "Мониторинг работает";
            else if (canEdit)
                monitorLabel.Text = "Мониторинг не запущен — включите «Автозапуск для всех пользователей»";
            else
                monitorLabel.Text = "Мониторинг не запущен — нужна настройка администратором";
            monitorLabel.ForeColor = running ? Color.DarkGreen : Color.Firebrick;
        }

        static string DescribeMode(bool enabled, bool block)
        {
            return !enabled ? "выкл." : block ? "завершение" : "журнал";
        }

        static bool Contains(string text, string part)
        {
            return text.IndexOf(part, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }
}
