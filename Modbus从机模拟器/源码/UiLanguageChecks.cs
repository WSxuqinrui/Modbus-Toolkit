using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// 检查独立运行，不打开串口、不改变系统区域设置、不弹出消息框。
internal static class UiLanguageChecks
{
    private static Type language;
    private static int passed;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    private static void Set(int value) { language.GetMethod("SetLanguage").Invoke(null, new object[] { value }); }
    private static void Apply(Control control) { language.GetMethod("Apply").Invoke(null, new object[] { control }); }
    private static string Tr(string value) { return (string)language.GetMethod("Translate").Invoke(null, new object[] { value }); }
    private sealed class Presentation
    {
        public string CustomName = "";
        public string GetName(int address) { return CustomName; }
    }
    private sealed class Area
    {
        public readonly Presentation Presentation = new Presentation();
    }
    // 与实际卡片的公开/私有显示契约一致；防止用户名称恰好等于译文键时被改动。
    private sealed class AddressCard : Panel
    {
        public readonly Button Header = new Button();
        public readonly Label ValueLabel = new Label();
        public readonly int Address = 3;
        private readonly Area area = new Area();
        public AddressCard(bool custom)
        {
            area.Presentation.CustomName = custom ? "保持寄存器" : "";
            Header.Text = "▸ 保持寄存器 · 3"; ValueLabel.Text = "123 保持寄存器";
            Controls.Add(Header); Controls.Add(ValueLabel);
        }
    }
    private sealed class TestForm : Form
    {
        public readonly ToolTip Hint = new ToolTip();
        protected override void Dispose(bool disposing) { if (disposing) Hint.Dispose(); base.Dispose(disposing); }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("Supply an assembly containing UiLanguage.");
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            language = assembly.GetType("ModbusSimulator.UiLanguage") ?? assembly.GetType("ModbusMaster.UiLanguage");
            if (language == null) throw new Exception("UiLanguage missing");
            string culture = System.Threading.Thread.CurrentThread.CurrentCulture.Name;
            using (TestForm form = new TestForm())
            {
                form.Size = new Size(700, 500);
                Label label = new Label { Text = "连接设置", Width = 72, Height = 25 };
                Button button = new Button { Text = "保存配置", Top = 30, Width = 72 };
                TextBox input = new TextBox { Text = "保持寄存器", Top = 60 };
                RichTextBox logs = new RichTextBox { Text = "保持寄存器 01 03 00 00", Top = 90, Height = 30 };
                ComboBox display = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Top = 130 };
                display.Items.AddRange(new string[] { "列表", "平铺窗格", "选项卡", "独立浮窗", "逐项卡片" }); display.SelectedIndex = 3;
                ComboBox parity = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; parity.Items.AddRange(new string[] { "None", "Even", "Odd" }); parity.SelectedIndex = 1;
                ComboBox types = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; types.Items.AddRange(new string[] { "UInt16", "Float32", "Float64" }); types.SelectedIndex = 1;
                ComboBox target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; target.Items.Add("COM7 保持寄存器"); target.SelectedIndex = 0;
                int selectionEvents = 0; display.SelectedIndexChanged += delegate { selectionEvents++; };
                ContextMenuStrip menu = new ContextMenuStrip(); ToolStripMenuItem item = new ToolStripMenuItem("单项解析设置…"); menu.Items.Add(item); button.ContextMenuStrip = menu;
                DataGridView grid = new DataGridView { Top = 180, Width = 600, Height = 140, AllowUserToAddRows = false };
                grid.Columns.Add("Area", "数据区 / 功能码"); grid.Columns.Add("Name", "名称 · 可编辑"); grid.Columns.Add("Unit", "单位 · 可编辑");
                grid.Rows.Add("保持寄存器", "", "保持寄存器"); grid.Rows.Add("线圈", "保持寄存器", "℃");
                grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e) { if (e.ColumnIndex == 1 && e.RowIndex == 0) { e.Value = "保持寄存器 · 3"; e.FormattingApplied = true; } };
                AddressCard custom = new AddressCard(true), defaults = new AddressCard(false); custom.Top = 330; defaults.Top = 390;
                form.Controls.AddRange(new Control[] { label, button, input, logs, display, parity, types, target, grid, custom, defaults });
                object tag = new object(); input.Tag = tag; form.Hint.SetToolTip(label, "应用数据区定义");
                Apply(form); form.CreateControl(); IntPtr unused = form.Handle;
                string[] expected = { "连接设置", "Connection", "接続設定", "연결 설정", "Подключение" };
                for (int index = 0; index < 5; index++)
                {
                    Set(index);
                    Check(label.Text == expected[index], "label language " + index);
                    Check(input.Text == "保持寄存器" && ReferenceEquals(input.Tag, tag), "input/tag preserved " + index);
                    Check(logs.Text == "保持寄存器 01 03 00 00", "logs preserved " + index);
                    Check(display.SelectedIndex == 3 && (string)display.Items[0] == "列表", "display enum unchanged " + index);
                    Check(selectionEvents == 0 && display.DrawMode == DrawMode.OwnerDrawFixed, "display event safety " + index);
                    Check(parity.Text == "Even" && types.Text == "Float32" && target.Text == "COM7 保持寄存器", "protocol/COM unchanged " + index);
                    Check(parity.DrawMode == DrawMode.Normal && types.DrawMode == DrawMode.Normal && target.DrawMode == DrawMode.Normal, "unsafe combo skipped " + index);
                    Check(custom.Header.Text == "▸ 保持寄存器 · 3" && custom.ValueLabel.Text == "123 保持寄存器", "custom card text preserved " + index);
                    Check(defaults.Header.Text == "▸ " + Tr("保持寄存器") + " · 3", "default card title " + index);
                    Check(grid.Columns[0].HeaderText == Tr("数据区 / 功能码"), "grid header " + index);
                    Check(Convert.ToString(grid[0, 0].FormattedValue) == Tr("保持寄存器"), "grid area display " + index);
                    Check(Convert.ToString(grid[1, 0].FormattedValue) == Tr("保持寄存器") + " · 3", "grid default name " + index);
                    Check(Convert.ToString(grid[1, 1].FormattedValue) == "保持寄存器" && Convert.ToString(grid[2, 0].FormattedValue) == "保持寄存器", "grid custom/unit preserved " + index);
                    Check(item.Text == Tr("单项解析设置…") && form.Hint.GetToolTip(label) == Tr("应用数据区定义"), "menu/tooltips " + index);
                    Check(label.AutoEllipsis && button.AutoEllipsis && display.DropDownWidth >= display.Width, "long text bounds " + index);
                    Check(Tr("Float32 / ABCD / COM7 / 01 03 00 00") == "Float32 / ABCD / COM7 / 01 03 00 00", "protocol literals " + index);
                    string summary = Tr("未连接 · 线圈：缓存  |  离散：未更新  |  保持：已更新  |  输入：上次值");
                    Check(summary.Contains(Tr("未连接")) && summary.Contains(Tr("离散")) && summary.Contains(Tr("输入")) && summary.Contains(Tr("缓存")) && summary.Contains(Tr("上次值")), "footer summary " + index);
                    string fullQuality = Tr("保持寄存器：整区未读取（本地缓存/配置快照）");
                    Check(fullQuality.Contains(Tr("整区未读取")) && fullQuality.Contains(Tr("配置快照")), "full quality tooltip " + index);
                    Check(index == 0 || (summary.IndexOf("上次值", StringComparison.Ordinal) < 0 && fullQuality.IndexOf("本地缓存", StringComparison.Ordinal) < 0), "footer translated " + index);
                    Check(index == 0 || Tr("循环发送已停止：连接或目标已改变，请重新选择。").IndexOf("循环发送", StringComparison.Ordinal) < 0, "cycle status translated " + index);
                    label.Text = "数据显示"; Check(label.Text == Tr("数据显示"), "new static state " + index); label.Text = "连接设置";
                }
                Set(4); Label dynamic = new Label { Text = "连接设置" }; form.Controls.Add(dynamic);
                Check(dynamic.Text == "Подключение", "dynamic control added");
                ToolStripMenuItem dynamicItem = new ToolStripMenuItem("取消"); menu.Items.Add(dynamicItem); Check(dynamicItem.Text == "Отмена", "dynamic menu added");
                Set(0); Check(dynamic.Text == "连接设置" && dynamicItem.Text == "取消" && label.Text == "连接设置", "Chinese round trip");
                Set(99); Check((int)language.GetProperty("Current").GetValue(null, null) == 0, "bad saved language fallback");
                Check(System.Threading.Thread.CurrentThread.CurrentCulture.Name == culture, "culture unchanged");
                Check(button.Width == 72 && label.Width == 72, "language does not grow fixed layout");
                menu.Dispose();
            }
            Console.WriteLine("UiLanguage checks passed: " + passed + "; five languages, dynamic controls/menus, editable data and protocol values preserved.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (language != null) Set(0); }
    }
}
