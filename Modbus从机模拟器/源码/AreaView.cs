using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ModbusSimulator
{
    // 设置面板与数据视图独立，视图可以安全移动到选项卡、平铺区或独立窗口。
    public sealed class AreaPanel : SmoothPanel
    {
        public static readonly string[] Names = { "线圈", "离散输入", "保持寄存器", "输入寄存器" };
        public static readonly string[] Functions = { "01 读 / 05、0F 写", "02 读 · 本地模拟", "03 读 / 06、10 写", "04 读 · 本地模拟" };
        public readonly int Area;
        public readonly DataGridView Grid = NewGrid();
        public readonly Panel Settings = new SmoothPanel();
        public readonly NumericUpDown StartInput = Number(0, 65535), CountInput = Number(0, 65536);
        public readonly ComboBox Format = new ComboBox();
        public readonly AreaPresentation Presentation = new AreaPresentation();
        private readonly TextBox fill = new TextBox { Text = "0", Dock = DockStyle.Fill };
        private readonly NumericUpDown jump = Number(0, 65535);
        private readonly Label summary = new Label();
        private readonly Action<string> log;
        private readonly TableLayoutPanel definition = new SmoothTable();
        private RegisterBank bank;
        private int format;
        private bool running;
        public event Action DefinitionChanged;
        public event Action DisplayFormatChanged;
        public Action<int> LocateRequested;
        public Func<bool> CommitOtherEdits;
        public AreaPanel(RegisterBank data, int area, Action<string> logger)
        {
            Area = area; bank = data; log = logger;
            Dock = DockStyle.Fill; BackColor = Color.White; Padding = new Padding(9); Margin = new Padding(4);
            TableLayoutPanel display = new SmoothTable { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            display.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            display.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); display.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); display.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            display.Controls.Add(new Label { Text = Names[area] + "   ·   " + Functions[area], Dock = DockStyle.Fill, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), ForeColor = Color.FromArgb(30, 64, 105) }, 0, 0);
            summary.Dock = DockStyle.Fill; summary.AutoEllipsis = true; summary.TextAlign = ContentAlignment.MiddleLeft; summary.ForeColor = Color.SlateGray;
            display.Controls.Add(Grid, 0, 1); display.Controls.Add(summary, 0, 2); Controls.Add(display);
            Grid.Columns.Add("Address", "协议地址"); Grid.Columns.Add("Reference", "参考编号"); Grid.Columns.Add("Value", "数值 · 双击编辑"); Grid.Columns.Add("Hex", "HEX 原始值");
            Grid.Columns.Add("Parsed", "解析值"); Grid.Columns[4].ReadOnly = true; Grid.Columns[4].SortMode = DataGridViewColumnSortMode.NotSortable; Grid.Columns[4].MinimumWidth = 100;
            for (int i = 0; i < 4; i++) { Grid.Columns[i].ReadOnly = i != 2; Grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; Grid.Columns[i].MinimumWidth = i == 2 ? 100 : 65; }
            Grid.Columns[2].FillWeight = 140;
            AddLabelColumns(Grid);
            Grid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (Grid.Disposing || Grid.IsDisposed || e.RowIndex < 0 || e.RowIndex >= bank.Count(Area)) return;
                int address = bank.Start(Area) + e.RowIndex;
                e.Value = e.ColumnIndex == 0 ? (object)address : e.ColumnIndex == 1 ? Reference(address) : e.ColumnIndex == 2 ? DisplayValue(address) : e.ColumnIndex == 3 ? "0x" + bank.Get(Area, address).ToString("X4") :
                    e.ColumnIndex == 4 ? ParsedValue(address) : e.ColumnIndex == 5 ? Presentation.GetName(address) : Presentation.GetUnit(address);
            };
            Grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (Grid.Disposing || Grid.IsDisposed) return;
                if (e.ColumnIndex == 5 && e.RowIndex >= 0 && e.RowIndex < bank.Count(Area) && String.IsNullOrEmpty(Convert.ToString(e.Value)))
                { e.Value = Names[Area] + " · " + (bank.Start(Area) + e.RowIndex); e.FormattingApplied = true; }
            };
            Grid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                if (Grid.Disposing || Grid.IsDisposed) return;
                // 默认标题仅用于显示，开始编辑时恢复实际空名称，避免固化默认标题。
                if (Grid.CurrentCell != null && Grid.CurrentCell.ColumnIndex == 5 && Grid.CurrentCell.RowIndex >= 0 && Grid.CurrentCell.RowIndex < bank.Count(Area))
                    e.Control.Text = Presentation.GetName(bank.Start(Area) + Grid.CurrentCell.RowIndex);
            };
            Grid.CellValuePushed += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (Grid.Disposing || Grid.IsDisposed || e.RowIndex < 0 || e.RowIndex >= bank.Count(Area)) return;
                if (e.ColumnIndex == 2) SetText(bank.Start(Area) + e.RowIndex, Convert.ToString(e.Value));
                else if (e.ColumnIndex == 5 || e.ColumnIndex == 6)
                    SetLabelText(bank.Start(Area) + e.RowIndex, e.ColumnIndex == 6, Convert.ToString(e.Value));
            };
            Grid.CellValidating += delegate(object sender, DataGridViewCellValidatingEventArgs e)
            {
                if (Grid.Disposing || Grid.IsDisposed || e.RowIndex < 0 || e.RowIndex >= Grid.RowCount) return;
                string error = GridTextError(e.ColumnIndex, Convert.ToString(e.FormattedValue));
                if (e.RowIndex >= 0 && error != null) { e.Cancel = true; Grid.Rows[e.RowIndex].ErrorText = error; }
            };
            Grid.CellEndEdit += delegate(object sender, DataGridViewCellEventArgs e) { if (!Grid.Disposing && !Grid.IsDisposed && e.RowIndex >= 0 && e.RowIndex < Grid.RowCount) Grid.Rows[e.RowIndex].ErrorText = ""; };
            if (Area >= 2)
            {
                Grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
                {
                    if (e.ColumnIndex == Grid.Columns["Parsed"].Index && e.RowIndex >= 0 && e.RowIndex < bank.Count(Area))
                        EditParseRule(bank.Start(Area) + e.RowIndex);
                };
                ContextMenuStrip itemMenu = new ContextMenuStrip();
                ToolStripMenuItem editRule = new ToolStripMenuItem("单项解析设置…"); itemMenu.Items.Add(editRule);
                int contextRow = -1;
                Grid.MouseDown += delegate(object sender, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Right) return;
                    contextRow = -1;
                    DataGridView.HitTestInfo hit = Grid.HitTest(e.X, e.Y);
                    if (hit.RowIndex < 0 || hit.RowIndex >= bank.Count(Area) || hit.ColumnIndex < 0 || !Commit()) return;
                    Grid.CurrentCell = Grid[hit.ColumnIndex, hit.RowIndex]; contextRow = hit.RowIndex;
                };
                itemMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
                { e.Cancel = contextRow < 0 || contextRow >= bank.Count(Area); };
                editRule.Click += delegate
                {
                    if (contextRow >= 0 && contextRow < bank.Count(Area)) EditParseRule(bank.Start(Area) + contextRow);
                };
                Grid.ContextMenuStrip = itemMenu;
                Grid.Disposed += delegate { itemMenu.Dispose(); };
            }
            Settings.Dock = DockStyle.Fill;
            // 每项输入独占一行，恢复清楚的纵向阅读顺序；外部侧栏负责整体滚动。
            // 寄存器区：9 个 34px 行 + 24px 提示 + 4px 顶部留白，共 334px。
            // 位区没有解析按钮及提示：8 个 34px 行 + 4px 顶部留白，共 276px。
            TableLayoutPanel options = new SmoothTable { Dock = DockStyle.Top, Height = Area >= 2 ? 334 : 276, ColumnCount = 2, RowCount = Area >= 2 ? 10 : 8, Padding = new Padding(0, 4, 4, 0) };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < options.RowCount; i++) options.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 9 ? 24 : 34));
            definition.Dock = DockStyle.Fill; definition.ColumnCount = 2; definition.RowCount = 3; definition.Margin = new Padding(0);
            definition.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); definition.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) definition.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            Setting(definition, 0, "起始地址：", StartInput); Setting(definition, 1, "数量：", CountInput);
            Button apply = Ui.Button("应用数据区定义", 160, true); apply.Dock = DockStyle.Fill; definition.Controls.Add(apply, 0, 2); definition.SetColumnSpan(apply, 2);
            options.Controls.Add(definition, 0, 0); options.SetColumnSpan(definition, 2); options.SetRowSpan(definition, 3);
            Format.Dock = DockStyle.Fill; Format.DropDownStyle = ComboBoxStyle.DropDownList;
            Format.Items.AddRange(area < 2 ? new string[] { "位值 0 / 1" } : new string[] { "无符号十进制", "有符号十进制", "十六进制" }); Format.SelectedIndex = 0;
            Setting(options, 3, "原始值格式", Format); Setting(options, 4, "批量初值", fill);
            Button same = Ui.Button("常数填充", 105, false), increment = Ui.Button("递增填充", 105, false); TableLayoutPanel filling = SidebarLayout.Actions(same, increment);
            options.Controls.Add(filling, 0, 5); options.SetColumnSpan(filling, 2);
            Button locate = Ui.Button("定位", 58, false); locate.Dock = DockStyle.Fill;
            Setting(options, 6, "跳转地址：", jump);
            options.Controls.Add(locate, 0, 7); options.SetColumnSpan(locate, 2);
            Settings.Controls.Add(options);
            // 位区仅显示 ON/OFF，不创建整区解析入口。
            if (Area >= 2)
            {
                Button parseDefault = Ui.Button("整区默认解析设置…", 160, false); parseDefault.Dock = DockStyle.Fill;
                options.Controls.Add(parseDefault, 0, 8); options.SetColumnSpan(parseDefault, 2);
                Label parseHint = new Label { Text = "双击解析值列修改公式", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.SlateGray };
                options.Controls.Add(parseHint, 0, 9); options.SetColumnSpan(parseHint, 2);
                parseDefault.Click += delegate
                {
                    using (RuleEditor dialog = new RuleEditor(Presentation.DefaultRule, Names[Area] + " · 整区默认解析（不覆盖单项规则）"))
                        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) { Presentation.DefaultRule = AreaPresentation.Copy(dialog.Result); NotifyPresentation(); }
                };
            }
            apply.Click += delegate
            {
                try
                {
                    int first = (int)StartInput.Value, count = (int)CountInput.Value;
                    if (count > RegisterBank.Capacity - first) throw new ArgumentException("起始地址 + 数量不得超过 65536。");
                    if ((first > bank.Start(Area) || first + count < bank.Start(Area) + bank.Count(Area)) &&
                        MessageBox.Show(this, "新范围外的数据会被移除，重叠地址值保留。是否继续？", "应用定义", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                    ApplyDefinition(first, count);
                }
                catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "定义无效"); }
            };
            same.Click += delegate { FillPrompt(false); }; increment.Click += delegate { FillPrompt(true); };
            locate.Click += delegate
            {
                if (Grid.RowCount == 0) { MessageBox.Show(this, "当前区未启用，没有可定位的地址。"); return; }
                int row = (int)jump.Value - bank.Start(Area);
                if (row < 0 || row >= Grid.RowCount) { MessageBox.Show(this, "地址不在当前区间内。"); return; }
                if (LocateRequested != null) { LocateRequested((int)jump.Value); return; }
                if (!Commit()) return; Grid.CurrentCell = Grid[2, row]; Grid.FirstDisplayedScrollingRowIndex = row;
                if (FindForm() != null) FindForm().Activate();
            };
            Format.SelectedIndexChanged += delegate
            {
                if (Format.SelectedIndex == format) return;
                if (Commit() && (CommitOtherEdits == null || CommitOtherEdits()))
                { format = Format.SelectedIndex; Grid.Invalidate(); if (DisplayFormatChanged != null) DisplayFormatChanged(); }
                else Format.SelectedIndex = format;
            };
            SidebarLayout.Normalize(definition, true); SidebarLayout.Normalize(options, true);
            Reload(data);
        }
        private static NumericUpDown Number(int min, int max) { return new NumericUpDown { Minimum = min, Maximum = max, Dock = DockStyle.Fill }; }
        internal static void Setting(TableLayoutPanel parent, int row, string label, Control control)
        { control.Dock = DockStyle.Fill; parent.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.SlateGray }, 0, row); parent.Controls.Add(control, 1, row); }
        public static DataGridView NewGrid()
        {
            DataGridView grid = new SmoothGrid { Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AllowUserToResizeRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                SelectionMode = DataGridViewSelectionMode.CellSelect, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 29 };
            grid.RowTemplate.Height = 25; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(237, 243, 251);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253); grid.GridColor = Color.FromArgb(229, 235, 242);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254); grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(23, 55, 100);
            grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; }; return grid;
        }
        public string Reference(int address)
        { int type = Area == 0 ? 0 : Area == 1 ? 1 : Area == 2 ? 4 : 3; return address < 9999 ? (type * 10000 + address + 1).ToString("D5") : type + ":" + (address + 1); }
        public string DisplayValue(int address)
        { ushort value = bank.Get(Area, address); return Area < 2 || format == 0 ? value.ToString() : format == 1 ? unchecked((short)value).ToString() : "0x" + value.ToString("X4"); }
        public string ParsedValue(int address)
        {
            if (Area < 2) return bank.Get(Area, address) == 0 ? "OFF" : "ON";
            try
            {
                ParseRule rule = Presentation.GetRule(address); double value = AreaPresentation.Read(bank, Area, address, rule);
                string unit = Presentation.GetUnit(address);
                return value.ToString("F" + rule.Decimals, CultureInfo.InvariantCulture) + (String.IsNullOrEmpty(unit) ? "" : " " + unit);
            }
            catch (ArgumentException) { return "尾部不足/规则无效"; }
        }
        public void SetParseRule(int address, ParseRule rule)
        {
            if (Area < 2) throw new InvalidOperationException("位区不支持解析规则设置。");
            rule.Validate(); if (address < bank.Start(Area) || address >= bank.Start(Area) + bank.Count(Area)) throw new ArgumentOutOfRangeException("address");
            Presentation.Rules[address] = AreaPresentation.Copy(rule); NotifyPresentation();
        }
        /// <summary>各数据视图共享的单项公式入口；位区及空区在显示对话框前拒绝。</summary>
        public void EditParseRule(int address)
        {
            if (Area < 2) throw new InvalidOperationException("位区不支持解析规则设置。");
            if (address < bank.Start(Area) || address >= bank.Start(Area) + bank.Count(Area)) throw new ArgumentOutOfRangeException("address");
            if (!Commit() || (CommitOtherEdits != null && !CommitOtherEdits())) return;
            using (RuleEditor dialog = new RuleEditor(Presentation.GetRule(address), Names[Area] + " · 地址 " + address + " · 单项解析"))
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) SetParseRule(address, dialog.Result);
        }
        public void NotifyPresentation() { Grid.Invalidate(); if (DisplayFormatChanged != null) DisplayFormatChanged(); }
        /// <summary>追加列保留既有数值/解析列索引，名称显示在最前方，适配各表格视图。</summary>
        internal static void AddLabelColumns(DataGridView grid)
        {
            grid.Columns.Add("Name", "名称 · 可编辑"); grid.Columns.Add("Unit", "单位 · 可编辑");
            DataGridViewTextBoxColumn name = (DataGridViewTextBoxColumn)grid.Columns["Name"], unit = (DataGridViewTextBoxColumn)grid.Columns["Unit"];
            name.MaxInputLength = 64; unit.MaxInputLength = 32;
            name.MinimumWidth = 115; name.FillWeight = 140; unit.MinimumWidth = 65; unit.FillWeight = 75;
            name.SortMode = unit.SortMode = DataGridViewColumnSortMode.NotSortable;
            name.ToolTipText = "本地名称，最多64个字符；留空使用数据区默认标题。";
            unit.ToolTipText = "本地显示单位，最多32个字符；留空隐藏单位，不写入设备。";
            name.DisplayIndex = 0; unit.DisplayIndex = grid.Columns["Value"].DisplayIndex + 1;
        }
        internal static string LabelTextError(string text, bool unit)
        { return (text ?? "").Trim().Length <= (unit ? 32 : 64) ? null : unit ? "单位最多32个字符；Esc 取消" : "名称最多64个字符；Esc 取消"; }
        private string GridTextError(int column, string text)
        {
            ushort value;
            if (column == 2 && !Parse(text, out value)) return "无效数值；Esc 取消";
            return column == 5 || column == 6 ? LabelTextError(text, column == 6) : null;
        }
        /// <summary>只更新所编辑的标签字段，另一字段始终读取当前共享值，避免旧视图缓存覆盖。</summary>
        public void SetLabelText(int address, bool unit, string text)
        {
            if (address < bank.Start(Area) || address >= bank.Start(Area) + bank.Count(Area) || LabelTextError(text, unit) != null) return;
            text = (text ?? "").Trim();
            Presentation.SetLabel(address, unit ? Presentation.GetName(address) : text, unit ? text : Presentation.GetUnit(address));
            NotifyPresentation();
        }
        public bool Parse(string text, out ushort value) { return ValueParser.TryParse(text, Area >= 2 && format == 1, out value) && (Area >= 2 || value <= 1); }
        public void SetText(int address, string text)
        { ushort value; if (Parse(text, out value)) { bank.Set(Area, address, value); log("本地编辑 " + Names[Area] + " 地址=" + address + " 值=" + value); } }
        public bool Commit()
        {
            if (Grid.IsCurrentCellInEditMode && Grid.EditingControl != null && Grid.CurrentCell != null)
            {
                string error = GridTextError(Grid.CurrentCell.ColumnIndex, Grid.EditingControl.Text);
                if (error != null) { Grid.CurrentRow.ErrorText = error; return false; }
            }
            return Grid.EndEdit();
        }
        public bool HasPendingDefinition { get { return StartInput.Value != bank.Start(Area) || CountInput.Value != bank.Count(Area); } }
        public void SetRunning(bool active) { running = active; definition.Enabled = !active; }
        public void Reload(RegisterBank replacement)
        {
            // 先回到第一行再重设行数，避免从高位地址缩小区间时逐行滚动造成长时间卡顿。
            Grid.CancelEdit(); Grid.CurrentCell = null;
            if (Grid.RowCount > 0 && Grid.Height > Grid.ColumnHeadersHeight + 4) Grid.FirstDisplayedScrollingRowIndex = 0;
            Grid.RowCount = 0; bank = replacement;
            StartInput.Value = bank.Start(Area); CountInput.Value = bank.Count(Area); Grid.RowCount = bank.Count(Area);
            summary.Text = bank.Count(Area) == 0 ? "未启用（数量为 0，无可用地址）" : "地址 " + bank.Start(Area) + " ～ " + (bank.Start(Area) + bank.Count(Area) - 1) + "   /   " + bank.Count(Area) + " 项";
            jump.Value = bank.Start(Area); Grid.Invalidate();
        }
        public void ApplyDefinition(int start, int count)
        {
            if (running) throw new InvalidOperationException("请先停止服务。"); if (!Commit() || (CommitOtherEdits != null && !CommitOtherEdits())) throw new ArgumentException("请完成或取消无效编辑。");
            bank.Configure(Area, start, count); Presentation.Trim(start, count); Reload(bank); log(Names[Area] + " 已定义：" + start + " / " + count); if (DefinitionChanged != null) DefinitionChanged();
        }
        public void FillValues(ushort value, bool increment)
        { if (!Commit() || (CommitOtherEdits != null && !CommitOtherEdits())) throw new ArgumentException("请先完成编辑。"); bank.Fill(Area, value, increment); Grid.Invalidate(); log(Names[Area] + " 批量填充 " + bank.Count(Area) + " 项"); }
        private void FillPrompt(bool increment)
        {
            ushort value; if (!Parse(fill.Text, out value)) { MessageBox.Show(this, "初值无效，HEX 请加 0x 前缀。"); return; }
            if (MessageBox.Show(this, "覆盖本区全部 " + bank.Count(Area) + " 项？递增到上限后从 0 循环。", "批量填充", MessageBoxButtons.OKCancel) == DialogResult.OK) FillValues(value, increment);
        }
        public void RefreshValues() { if (Visible && !Grid.IsCurrentCellInEditMode) Grid.Invalidate(); }
    }
    internal static class Ui
    {
        public static Button Button(string text, int width, bool primary)
        {
            Button button = new Button { Text = text, Width = width, Height = 28, Margin = new Padding(0, 1, 6, 0), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(51, 65, 85), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225); button.FlatAppearance.BorderSize = primary ? 0 : 1; return button;
        }
    }
}
