using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
namespace ModbusMaster
{
    public sealed class WriteDialog : Form
    {
        public int Area { get; private set; }
        public int Address { get; private set; }
        public ushort[] Values { get; private set; }
        public bool Multiple { get; private set; }
        private readonly ComboBox function = new ComboBox(), format = new ComboBox();
        private readonly NumericUpDown address = new NumericUpDown { Maximum = 65535 };
        private readonly TextBox values = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Text = "100" };
        public WriteDialog()
        {
            Text = "写入设备 · 确认后读回"; Font = new Font("Microsoft YaHei UI", 9); ClientSize = new Size(560, 355); MinimumSize = new Size(510, 340); StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel table = new SmoothTable { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 36, 36, 36 }) table.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            function.Items.AddRange(new string[] { "05 · 写单个线圈", "06 · 写单个保持寄存器", "0F · 写多个线圈", "10 · 写多个保持寄存器" }); function.DropDownStyle = ComboBoxStyle.DropDownList; function.SelectedIndex = 1;
            format.Items.AddRange(new string[] { "无符号 / 0x十六进制", "有符号16位 / 0x十六进制" }); format.DropDownStyle = ComboBoxStyle.DropDownList; format.SelectedIndex = 0;
            AreaPanel.Setting(table, 0, "功能码", function); AreaPanel.Setting(table, 1, "协议地址", address); AreaPanel.Setting(table, 2, "输入格式", format); AreaPanel.Setting(table, 3, "写入数值", values);
            Label hint = new Label { Text = "多个数值用空格、逗号或换行分隔；线圈只能填0/1。\r\n写入范围须位于已配置的数据区内；浮点/工程值可直接在卡片写入。", Dock = DockStyle.Fill, ForeColor = Color.SlateGray };
            table.Controls.Add(hint, 0, 4); table.SetColumnSpan(hint, 2);
            FlowLayoutPanel buttons = new SmoothFlow { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; Button apply = Ui.Button("发送写入", 110, true), cancel = Ui.Button("取消", 80, false); cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(apply); buttons.Controls.Add(cancel); table.Controls.Add(buttons, 0, 5); table.SetColumnSpan(buttons, 2); Controls.Add(table); AcceptButton = apply; CancelButton = cancel;
            function.SelectedIndexChanged += delegate { if (function.SelectedIndex == 0 || function.SelectedIndex == 2) values.Text = "1"; else values.Text = "100"; };
            apply.Click += delegate
            {
                try
                {
                    Area = function.SelectedIndex == 0 || function.SelectedIndex == 2 ? 0 : 2; Multiple = function.SelectedIndex >= 2; Address = (int)address.Value;
                    Values = ParseValues(values.Text, Area == 0, format.SelectedIndex == 1);
                    if (!Multiple && Values.Length != 1) throw new ArgumentException("单个写入只能填写一个数值。");
                    if (Values.Length > 65536 - Address) throw new ArgumentException("起始地址+写入数量超过65536。"); DialogResult = DialogResult.OK;
                }
                catch (ArgumentException ex) { LocalizedMessageBox.Show(this, ex.Message, "写入参数"); }
            };
            UiLanguage.Apply(this); UiTheme.Apply(this);
        }
        public static ushort[] ParseValues(string text, bool bits, bool signed)
        {
            string[] parts = (text ?? "").Split(new char[] { ' ', ',', '，', ';', '；', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 65536) throw new ArgumentException("请填写1～65536个数值。");
            List<ushort> result = new List<ushort>(); foreach (string part in parts)
            { ushort value; if (!ValueParser.TryParse(part, signed && !bits, out value) || (bits && value > 1)) throw new ArgumentException("无效写入值：" + part); result.Add(value); }
            return result.ToArray();
        }
    }
}
