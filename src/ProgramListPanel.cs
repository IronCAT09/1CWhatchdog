using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OneCWhatchdog
{
    /// <summary>
    /// Вкладка со списком программ: чекбокс включения, режим «завершать / только журнал»,
    /// список exe и кнопки пополнения. Используется для разрешённых и запрещённых программ.
    /// </summary>
    sealed class ProgramListPanel : Panel
    {
        readonly bool canEdit;
        readonly bool includeTrustedRunning;
        readonly CheckBox enabledBox = new CheckBox();
        readonly CheckBox blockBox = new CheckBox();
        readonly ListBox list = new ListBox();
        readonly TextBox nameBox = new TextBox();

        List<string> names = new List<string>();
        bool updating;

        /// <summary>Спрашивается при включении завершения программ; false — отменить включение.</summary>
        public Func<bool> ConfirmBlocking;

        /// <summary>Пользователь изменил режим или список — нужно сохранить.</summary>
        public event EventHandler Changed;

        /// <param name="includeTrustedRunning">показывать в «Из запущенных…» и программы из папки Windows.</param>
        public ProgramListPanel(string enableText, string blockText, string infoText, bool canEdit, bool includeTrustedRunning)
        {
            this.canEdit = canEdit;
            this.includeTrustedRunning = includeTrustedRunning;
            Dock = DockStyle.Fill;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3, 4, 3, 0) };
            enabledBox.Text = enableText;
            enabledBox.AutoSize = true;
            enabledBox.CheckedChanged += delegate { OnModeChanged(); };
            blockBox.Text = blockText;
            blockBox.AutoSize = true;
            blockBox.Margin = new Padding(16, 3, 3, 3);
            blockBox.CheckedChanged += delegate { OnModeChanged(); };
            top.Controls.Add(enabledBox);
            top.Controls.Add(blockBox);

            var info = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 40,
                Padding = new Padding(6, 4, 6, 0),
                ForeColor = SystemColors.GrayText,
                Text = infoText
            };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(3, 4, 3, 4) };
            var nameLabel = new Label { Text = "Имя exe:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) };
            nameBox.Width = 200;
            nameBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    Add(new[] { nameBox.Text });
                    e.SuppressKeyPress = true;
                }
            };
            var addButton = new Button { Text = "Добавить", AutoSize = true };
            addButton.Click += delegate { Add(new[] { nameBox.Text }); };
            var fileButton = new Button { Text = "Выбрать файл…", AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
            fileButton.Click += delegate { AddFromFiles(); };
            var runningButton = new Button { Text = "Из запущенных…", AutoSize = true };
            runningButton.Click += delegate { AddFromRunning(); };
            var removeButton = new Button { Text = "Удалить", AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
            removeButton.Click += delegate { RemoveSelected(); };
            bottom.Controls.AddRange(new Control[] { nameLabel, nameBox, addButton, fileButton, runningButton, removeButton });

            list.Dock = DockStyle.Fill;
            list.IntegralHeight = false;
            list.SelectionMode = SelectionMode.MultiExtended;
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete)
                    RemoveSelected();
            };

            foreach (Control c in new Control[] { enabledBox, blockBox, nameBox, addButton, fileButton, runningButton, removeButton })
                c.Enabled = canEdit;

            // Порядок важен: Fill первым, затем нижняя панель, затем верхние (последний — самый верхний).
            Controls.Add(list);
            Controls.Add(bottom);
            Controls.Add(info);
            Controls.Add(top);
        }

        public bool ListEnabled { get; private set; }
        public bool Block { get; private set; }

        public List<string> Names
        {
            get { return new List<string>(names); }
        }

        /// <summary>Показать сохранённые настройки (без события Changed).</summary>
        public void SetData(bool enabled, bool block, IEnumerable<string> entries)
        {
            ListEnabled = enabled;
            Block = block;
            names = entries.ToList();
            updating = true;
            try
            {
                enabledBox.Checked = enabled;
                blockBox.Checked = block;
                FillList();
            }
            finally
            {
                updating = false;
            }
        }

        void FillList()
        {
            var selected = new HashSet<string>(list.SelectedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    int index = list.Items.Add(name);
                    if (selected.Contains(name))
                        list.SetSelected(index, true);
                }
            }
            finally
            {
                list.EndUpdate();
            }
            blockBox.Enabled = canEdit && ListEnabled;
        }

        void OnModeChanged()
        {
            if (updating || !canEdit)
                return;

            bool enabled = enabledBox.Checked;
            bool block = blockBox.Checked;
            bool blockingNow = enabled && block;
            bool blockingBefore = ListEnabled && Block;
            if (blockingNow && !blockingBefore && ConfirmBlocking != null && !ConfirmBlocking())
            {
                updating = true;
                try
                {
                    enabledBox.Checked = ListEnabled;
                    blockBox.Checked = Block;
                }
                finally
                {
                    updating = false;
                }
                return;
            }

            ListEnabled = enabled;
            Block = block;
            blockBox.Enabled = enabled;
            RaiseChanged();
        }

        void Add(IEnumerable<string> entries)
        {
            if (!canEdit)
                return;
            var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (var entry in entries)
            {
                string name = AppControl.NormalizeName(entry);
                if (name.Length > 0 && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && current.Add(name))
                {
                    names.Add(name);
                    changed = true;
                }
            }
            nameBox.Clear();
            if (!changed)
                return;
            FillList();
            RaiseChanged();
        }

        void AddFromFiles()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Выбор программ",
                Filter = "Программы (*.exe)|*.exe",
                Multiselect = true
            })
            {
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    Add(dialog.FileNames);
            }
        }

        void AddFromRunning()
        {
            var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var running = AppControl.GetRunningPrograms(includeTrustedRunning)
                .Where(kv => !current.Contains(kv.Key)).ToList();
            if (running.Count == 0)
            {
                MessageBox.Show(FindForm(), "Все запущенные программы уже есть в списке.", FindForm().Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new Form
            {
                Text = "Запущенные программы",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(640, 460),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false
            })
            {
                var hint = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(6, 8, 6, 0),
                    Text = "Отметьте программы, которые нужно добавить в список:"
                };
                var choices = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
                foreach (var kv in running)
                    choices.Items.Add(kv.Key + "    —    " + kv.Value);

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    AutoSize = true,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(6)
                };
                var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
                var ok = new Button { Text = "Добавить", AutoSize = true, DialogResult = DialogResult.OK };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                dialog.Controls.Add(choices);
                dialog.Controls.Add(hint);
                dialog.Controls.Add(buttons);

                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    Add(choices.CheckedIndices.Cast<int>().Select(i => running[i].Key).ToList());
            }
        }

        void RemoveSelected()
        {
            if (!canEdit || list.SelectedItems.Count == 0)
                return;
            var remove = new HashSet<string>(list.SelectedItems.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            names = names.Where(n => !remove.Contains(n)).ToList();
            list.ClearSelected();
            FillList();
            RaiseChanged();
        }

        void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }
}
