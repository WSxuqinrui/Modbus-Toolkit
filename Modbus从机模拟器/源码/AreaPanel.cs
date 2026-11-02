using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ModbusSimulator
{
    // 虚拟表格只按需读取可见单元格，支持完整 65536 个地址而不创建大量行对象。
    public sealed class AreaPanel : Panel
    {
        public readonly int Area;
        public readonly DataGridView Grid = new DataGridView();
        public readonly NumericUpDown StartInput = new NumericUpDown(), CountInput = new NumericUpDown();
        public readonly ComboBox Format = new ComboBox();
        private readonly FlowLayoutPanel definition = new FlowLayoutPanel();
        private readonly TextBox fillValue = new TextBox();
        private readonly NumericUpDown jump = new NumericUpDown();
        private readonly Label summary = new Label();
        private readonly Action<string> log;
        private RegisterBank bank;
        private int displayFormat;
        public AreaPanel(RegisterBank data, int area, Action<string> logger)
        {
            Area = area; bank = data; log = logger;
            Dock = DockStyle.Fill; Margin = new Padding(5); Padding = new Padding(10, 7, 10, 7); BackColor = Color.White;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 28, 33, 33 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); Controls.Add(layout);
            string[] titles = { "线圈", "离散输入", "保持寄存器", "输入寄存器" };
            string[] functions = { "01 读 / 05、0F 写", "02 读 · 本地编辑模拟", "03 读 / 06、10 写", "04 读 · 本地编辑模拟" };
            layout.Controls.Add(new Label { Text = titles[area] + "   ·   " + functions[area], Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), ForeColor = Color.FromArgb(27, 60, 102), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            definition.Dock = DockStyle.Fill; definition.WrapContents = false; definition.Margin = new Padding(0);
            Caption(definition, "起始地址"); Number(StartInput, 0, 65535, bank.Start(area), 73); definition.Controls.Add(StartInput);
            Caption(definition, "数量"); Number(CountInput, 1, 65536, bank.Count(area), 83); definition.Controls.Add(CountInput);
            Button apply = Ui.Button("应用定义", 83, false); definition.Controls.Add(apply); apply.Click += delegate { ApplyWithPrompt(); };
            layout.Controls.Add(definition, 0, 1);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            Format.DropDownStyle = ComboBoxStyle.DropDownList;
            Format.Items.AddRange(area < 2 ? new string[] { "位值 0 / 1" } : new string[] { "无符号十进制", "有符号十进制", "十六进制" });
            Format.SelectedIndex = 0; Format.Width = 122; Format.Margin = new Padding(0, 2, 7, 0); actions.Controls.Add(Format);
            fillValue.Text = "0"; fillValue.Width = 76; fillValue.Margin = new Padding(0, 2, 6, 0); fillValue.AccessibleName = "批量填充值"; actions.Controls.Add(fillValue);
            Button fill = Ui.Button("填充", 56, false), sequence = Ui.Button("递增", 56, false);
            actions.Controls.Add(fill); actions.Controls.Add(sequence); fill.Click += delegate { FillWithPrompt(false); }; sequence.Click += delegate { FillWithPrompt(true); };
            layout.Controls.Add(actions, 0, 2);
            Grid.Dock = DockStyle.Fill; Grid.Margin = new Padding(0, 2, 0, 0); Grid.VirtualMode = true;
            Grid.AllowUserToAddRows = false; Grid.AllowUserToDeleteRows = false; Grid.AllowUserToResizeRows = false; Grid.RowHeadersVisible = false;
            Grid.BackgroundColor = Color.White; Grid.BorderStyle = BorderStyle.None; Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            Grid.ColumnHeadersHeight = 28; Grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            Grid.RowTemplate.Height = 24; Grid.EnableHeadersVisualStyles = false;
            Grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(237, 243, 251); Grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 76, 103);
            Grid.GridColor = Color.FromArgb(229, 235, 242); Grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
            Grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254); Grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(23, 55, 100);
            Grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            Grid.Columns.Add("Address", "协议地址"); Grid.Columns.Add("Reference", "参考编号"); Grid.Columns.Add("Value", "数值 · 双击编辑"); Grid.Columns.Add("Hex", "HEX 原始值");
            Grid.Columns[0].FillWeight = 75; Grid.Columns[1].FillWeight = 78; Grid.Columns[2].FillWeight = 126; Grid.Columns[3].FillWeight = 90;
            for (int i = 0; i < 4; i++) { Grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; Grid.Columns[i].ReadOnly = i != 2; }
            Grid.CellValueNeeded += ValueNeeded; Grid.CellValuePushed += ValuePushed; Grid.CellValidating += ValidateValue;
            Grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            Grid.CellEndEdit += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Grid.Rows[e.RowIndex].ErrorText = ""; };
            Format.SelectedIndexChanged += delegate
            {
                if (Format.SelectedIndex == displayFormat) return;
                if (Commit()) { displayFormat = Format.SelectedIndex; Grid.Invalidate(); }
                else Format.SelectedIndex = displayFormat;
            };
            layout.Controls.Add(Grid, 0, 3);
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            Caption(footer, "跳转"); Number(jump, 0, 65535, 0, 76); footer.Controls.Add(jump);
            Button locate = Ui.Button("定位", 52, false); footer.Controls.Add(locate);
            locate.Click += delegate
            {
                int row = (int)jump.Value - bank.Start(Area);
                if (row >= 0 && row < Grid.RowCount && Commit()) { Grid.CurrentCell = Grid[2, row]; Grid.FirstDisplayedScrollingRowIndex = row; }
                else if (row < 0 || row >= Grid.RowCount) MessageBox.Show(this, "地址不在当前区间内。", "定位");
            };
            summary.AutoSize = true; summary.ForeColor = Color.FromArgb(100, 116, 139); summary.Margin = new Padding(7, 5, 0, 0);
            footer.Controls.Add(summary); layout.Controls.Add(footer, 0, 4); Reload(bank);
        }
        private static void Number(NumericUpDown control, int min, int max, int value, int width)
        { control.Minimum = min; control.Maximum = max; control.Value = value; control.Width = width; control.Margin = new Padding(0, 2, 7, 0); }
        private static void Caption(FlowLayoutPanel panel, string text)
        { panel.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 5, 0) }); }
        public bool Commit()
        {
            if (Grid.IsCurrentCellInEditMode && Grid.EditingControl != null)
            {
                ushort value;
                if (!Parse(Grid.EditingControl.Text, out value))
                {
                    Grid.CurrentRow.ErrorText = Area < 2 ? "仅允许 0 / 1；Esc 取消" : "请输入有效 16 位整数；Esc 取消";
                    return false;
                }
            }
            return Grid.EndEdit();
        }
        public bool HasPendingDefinition { get { return (int)StartInput.Value != bank.Start(Area) || (int)CountInput.Value != bank.Count(Area); } }
        public void SetRunning(bool running) { definition.Enabled = !running; }
        public void Reload(RegisterBank replacement)
        {
            Grid.CancelEdit(); bank = replacement; StartInput.Value = bank.Start(Area); CountInput.Value = bank.Count(Area); Grid.RowCount = bank.Count(Area);
            summary.Text = bank.Start(Area) + " ～ " + (bank.Start(Area) + bank.Count(Area) - 1) + " · " + bank.Count(Area) + " 项";
            jump.Value = bank.Start(Area); Grid.Invalidate();
        }
        public void ApplyDefinition(int start, int count)
        {
            if (!definition.Enabled) throw new InvalidOperationException("请先停止服务再修改定义。");
            if (!Commit()) throw new ArgumentException("请完成或取消无效单元格编辑。");
            bank.Configure(Area, start, count); Reload(bank); log("数据区 " + Area + " 已定义：起始 " + start + "，数量 " + count);
        }
        private void ApplyWithPrompt()
        {
            try
            {
                int start = (int)StartInput.Value, count = (int)CountInput.Value;
                if (count > RegisterBank.Capacity - start) throw new ArgumentException("起始地址 + 数量不能超过 65536。");
                bool discards = start > bank.Start(Area) || start + count < bank.Start(Area) + bank.Count(Area);
                if (discards && MessageBox.Show(this, "新范围之外的数据会被移除，重叠地址的数据会保留。\r\n继续应用？", "修改数据区", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                ApplyDefinition(start, count);
            }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "数据区定义", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        public void FillValues(ushort first, bool increment)
        {
            if (!Commit()) throw new ArgumentException("请完成或取消无效单元格编辑。");
            bank.Fill(Area, first, increment); Grid.Invalidate(); log("数据区 " + Area + " " + (increment ? "递增" : "常数") + "填充 " + bank.Count(Area) + " 项，起始值 " + first);
        }
        private void FillWithPrompt(bool increment)
        {
            ushort value;
            if (!ValueParser.TryParse(fillValue.Text, Area >= 2 && displayFormat == 1, out value) || (Area < 2 && value > 1))
            { MessageBox.Show(this, Area < 2 ? "请输入 0 或 1。" : "请输入有效 16 位数值，HEX 加 0x 前缀。", "填充值无效"); return; }
            if (MessageBox.Show(this, "将覆盖本区全部 " + bank.Count(Area) + " 项数据。\r\n" + (increment ? "递增值到上限后从 0 循环。" : "使用相同数值填充。"), "批量填充", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { FillValues(value, increment); } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message); }
        }
        private void ValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= bank.Count(Area)) return;
            int address = bank.Start(Area) + e.RowIndex; ushort value = bank.Get(Area, address);
            switch (e.ColumnIndex)
            {
                case 0: e.Value = address; break;
                case 1:
                    string prefix = Area == 0 ? "0" : Area == 1 ? "1" : Area == 2 ? "4" : "3";
                    e.Value = address < 9999 ? (int.Parse(prefix) * 10000 + address + 1).ToString("D5") : prefix + ":" + (address + 1); break;
                case 2: e.Value = Area < 2 || displayFormat == 0 ? value.ToString(CultureInfo.InvariantCulture) :
                    displayFormat == 1 ? unchecked((short)value).ToString(CultureInfo.InvariantCulture) : "0x" + value.ToString("X4"); break;
                case 3: e.Value = "0x" + value.ToString("X4"); break;
            }
        }
        private bool Parse(object input, out ushort value)
        { return ValueParser.TryParse(Convert.ToString(input), Area >= 2 && displayFormat == 1, out value) && (Area >= 2 || value <= 1); }
        private void ValidateValue(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (e.ColumnIndex != 2) return; ushort value;
            if (!Parse(e.FormattedValue, out value)) { e.Cancel = true; Grid.Rows[e.RowIndex].ErrorText = Area < 2 ? "仅允许 0 / 1；Esc 取消" : "请输入有效 16 位整数；HEX 加 0x，Esc 取消"; }
            else Grid.Rows[e.RowIndex].ErrorText = "";
        }
        private void ValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            ushort value;
            if (e.ColumnIndex == 2 && Parse(e.Value, out value))
            { bank.Set(Area, bank.Start(Area) + e.RowIndex, value); log("本地编辑 区=" + Area + " 地址=" + (bank.Start(Area) + e.RowIndex) + " 值=" + value); }
        }
        public void RefreshValues() { if (!Grid.IsCurrentCellInEditMode) Grid.Invalidate(); }
    }
    internal static class Ui
    {
        public static Button Button(string text, int width, bool primary)
        {
            Button button = new Button { Text = text, Width = width, Height = 27, Margin = new Padding(0, 1, 6, 0),
                FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(51, 65, 85), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225); button.FlatAppearance.BorderSize = primary ? 0 : 1; return button;
        }
    }
}
