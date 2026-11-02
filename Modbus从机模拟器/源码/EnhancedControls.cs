using System;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ModbusSimulator
{
    public class SmoothPanel : Panel { public SmoothPanel() { DoubleBuffered = true; } }
    internal class SmoothTable : TableLayoutPanel { public SmoothTable() { DoubleBuffered = true; } }
    internal class SmoothFlow : FlowLayoutPanel { public SmoothFlow() { DoubleBuffered = true; } }
    internal class SmoothSplit : SplitContainer { public SmoothSplit() { DoubleBuffered = true; } }
    internal class SmoothGrid : DataGridView { public SmoothGrid() { DoubleBuffered = true; } }

    // 普通Panel的DoubleBuffered不覆盖子HWND；卡片视口单独合成子控件，避免滚动拷贝旧像素。
    internal sealed class CardViewport : SmoothPanel
    {
        private int frameDepth;
        private IntPtr frozenWindow;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= 0x02000000; return parameters; }
        }
        public void BeginFrame()
        {
            if (frameDepth++ != 0 || !IsHandleCreated || !Visible) return;
            frozenWindow = Handle; SendMessage(frozenWindow, 0x000B, IntPtr.Zero, IntPtr.Zero);
        }
        public void EndFrame()
        {
            if (frameDepth == 0 || --frameDepth != 0) return;
            IntPtr window = frozenWindow; frozenWindow = IntPtr.Zero;
            if (window == IntPtr.Zero || !IsHandleCreated || IsDisposed || Handle != window) return;
            SendMessage(window, 0x000B, new IntPtr(1), IntPtr.Zero);
            // 一次失效整张视口和所有子控件，由消息队列合并绘制，不逐张强制Refresh。
            RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080);
        }
    }

    // RichEdit原生执行视觉换行，原始记录不插入软换行，避免超长报文被固定行高截断。
    public sealed class TrafficLog : RichTextBox
    {
        private readonly Queue<int> recordLengths = new Queue<int>();
        private const int MaximumRecords = 1000;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        public int RecordCount { get { return recordLengths.Count; } }
        public TrafficLog()
        {
            ReadOnly = true; WordWrap = true; DetectUrls = false; HideSelection = false;
            ScrollBars = RichTextBoxScrollBars.Vertical; BorderStyle = BorderStyle.None;
            BackColor = Color.White; Font = new Font("Consolas", 9); MaxLength = int.MaxValue;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("复制选中日志   Ctrl+C", null, delegate { Copy(false); });
            menu.Items.Add("复制全部日志", null, delegate { Copy(true); });
            menu.Items.Add("全选   Ctrl+A", null, delegate { SelectAll(); });
            ContextMenuStrip = menu;
        }
        public new void Clear() { recordLengths.Clear(); base.Clear(); }
        /// <summary>UI线程批量追加；折叠时仍收集。选择文本期间保留选择和阅读位置。</summary>
        public void AppendLines(IEnumerable<string> lines)
        {
            int oldStart = SelectionStart, oldLength = SelectionLength, removed = 0;
            int anchor = IsHandleCreated ? GetCharIndexFromPosition(new Point(1, 1)) : 0;
            bool freeze = IsHandleCreated && Visible;
            if (freeze) SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
            try
            {
                foreach (string entry in lines)
                {
                    string line = entry ?? "";
                    // RichEdit以LF存储文本；记录长度以真实写入量为准，兼容输入中的CRLF。
                    Select(TextLength, 0); SelectionColor = LineColor(line); int before = TextLength;
                    AppendText(line + "\n"); recordLengths.Enqueue(TextLength - before);
                }
                while (recordLengths.Count > MaximumRecords) removed += recordLengths.Dequeue();
                if (removed != 0)
                {
                    // RichEdit在只读状态下会忽略选区删除；仅在本次同步清理期间解除只读。
                    ReadOnly = false;
                    try { Select(0, removed); SelectedText = ""; }
                    finally { ReadOnly = true; }
                }
                if (oldLength == 0) { Select(TextLength, 0); ScrollToCaret(); }
                else
                {
                    int start = Math.Max(0, oldStart - removed), end = Math.Max(start, oldStart + oldLength - removed);
                    Select(Math.Min(start, TextLength), Math.Min(end, TextLength) - Math.Min(start, TextLength));
                    if (IsHandleCreated)
                    {
                        int firstLine = GetLineFromCharIndex(Math.Max(0, Math.Min(TextLength, anchor - removed)));
                        int current = SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                        SendMessage(Handle, 0x00B6, IntPtr.Zero, new IntPtr(firstLine - current));
                    }
                }
            }
            finally { if (freeze && !IsDisposed) { SendMessage(Handle, 0x000B, new IntPtr(1), IntPtr.Zero); Invalidate(); } }
        }
        public static Color LineColor(string line)
        {
            if (line.Contains(" RX:") || line.StartsWith("RX:", StringComparison.Ordinal)) return UiTheme.LogRx;
            if (line.Contains(" TX:") || line.StartsWith("TX:", StringComparison.Ordinal)) return UiTheme.LogTx;
            if (line.Contains("失败") || line.Contains("错误") || line.Contains("异常") || line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) return UiTheme.ErrorText;
            return UiTheme.LogDefault;
        }
        /// <summary>重着色已有记录，保留原文、选区和阅读位置；仅在显式主题应用时调用。</summary>
        public void ApplyTheme()
        {
            int oldStart = SelectionStart, oldLength = SelectionLength;
            int firstLine = IsHandleCreated ? SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32() : 0;
            bool freeze = IsHandleCreated && Visible;
            if (freeze) SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
            try
            {
                BackColor = UiTheme.Surface; string text = Text; int position = 0;
                foreach (int length in recordLengths)
                {
                    int count = Math.Min(length, text.Length - position); if (count <= 0) break;
                    Select(position, count); SelectionColor = LineColor(text.Substring(position, count)); SelectionBackColor = BackColor; position += count;
                }
                Select(Math.Min(oldStart, TextLength), Math.Min(oldLength, Math.Max(0, TextLength - oldStart)));
                if (IsHandleCreated)
                { int now = SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32(); SendMessage(Handle, 0x00B6, IntPtr.Zero, new IntPtr(firstLine - now)); }
            }
            finally { if (freeze && !IsDisposed) { SendMessage(Handle, 0x000B, new IntPtr(1), IntPtr.Zero); Invalidate(); } }
        }
        public string CopyText(bool all)
        {
            return (all ? Text : SelectedText).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }
        public void Copy(bool all)
        {
            string text = CopyText(all); if (text.Length == 0) return;
            try { Clipboard.SetText(text); } catch (ExternalException) { }
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { Copy(false); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        { if (disposing && ContextMenuStrip != null) ContextMenuStrip.Dispose(); base.Dispose(disposing); }
    }
    public sealed class RuleEditor : Form
    {
        private readonly ComboBox type = new ComboBox(), order = new ComboBox(), operation = new ComboBox();
        private readonly TextBox operand = new TextBox(), offset = new TextBox(), unit = new TextBox();
        private readonly NumericUpDown decimals = new NumericUpDown();
        public ParseRule Result { get; private set; }
        public RuleEditor(ParseRule initial, string title)
        {
            Text = title; Font = new Font("Microsoft YaHei UI", 9); ClientSize = new Size(490, 410); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            SmoothTable table = new SmoothTable { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 10 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); Controls.Add(table);
            Setup(type, new string[] { "UInt16", "Int16", "UInt32", "Int32", "Float32", "Float64" }, initial.DataType);
            Setup(order, new string[] { "ABCD", "BADC", "CDAB", "DCBA" }, initial.ByteOrder);
            Setup(operation, new string[] { "+", "-", "*", "/" }, initial.Operation);
            operand.Text = initial.Operand.ToString("R", System.Globalization.CultureInfo.InvariantCulture); offset.Text = initial.Offset.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            unit.Text = initial.Unit; unit.MaxLength = 32; decimals.Minimum = 0; decimals.Maximum = 12; decimals.Value = initial.Decimals;
            AreaPanel.Setting(table, 0, "数据类型", type); AreaPanel.Setting(table, 1, "字节顺序", order);
            AreaPanel.Setting(table, 2, "运算符", operation); AreaPanel.Setting(table, 3, "运算参数", operand);
            AreaPanel.Setting(table, 4, "运算后偏移", offset); AreaPanel.Setting(table, 5, "小数位数", decimals); AreaPanel.Setting(table, 6, "显示单位", unit);
            Label formula = new Label { Text = "显示值 = (原始值 运算符 参数) + 偏移\r\nABCD 大端 / DCBA 小端 / BADC 字节交换 / CDAB 字交换", Dock = DockStyle.Fill, ForeColor = Color.SlateGray };
            table.Controls.Add(formula, 0, 7); table.SetColumnSpan(formula, 2); table.SetRowSpan(formula, 2);
            SmoothFlow buttons = new SmoothFlow { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button apply = Ui.Button("应用规则", 108, true), cancel = Ui.Button("取消", 90, false); cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(apply); buttons.Controls.Add(cancel); table.Controls.Add(buttons, 0, 9); table.SetColumnSpan(buttons, 2);
            AcceptButton = apply; CancelButton = cancel;
            apply.Click += delegate
            {
                try
                {
                    double factor, shift;
                    if (!double.TryParse(operand.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out factor) ||
                        !double.TryParse(offset.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out shift)) throw new ArgumentException("运算参数和偏移必须是有效数值。");
                    Result = new ParseRule { DataType = type.Text, ByteOrder = order.Text, Operation = operation.Text, Operand = factor, Offset = shift, Decimals = (int)decimals.Value, Unit = unit.Text };
                    Result.Validate(); DialogResult = DialogResult.OK;
                }
                catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "解析规则无效"); }
            };
            UiTheme.Apply(this); UiLanguage.Apply(this);
        }
        private static void Setup(ComboBox box, string[] items, string initial)
        { box.Items.AddRange(items); box.SelectedItem = initial; box.DropDownStyle = ComboBoxStyle.DropDownList; box.Dock = DockStyle.Fill; }
    }
}
