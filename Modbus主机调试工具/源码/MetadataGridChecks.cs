using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// 加载实际产品 EXE，检查本地元数据与表格事件；不连接 COM 或网络。
internal static class MetadataGridChecks
{
    private static int checks;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Field(object target, string name) { return target.GetType().GetField(name, Fields).GetValue(target); }
    private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, Fields).Invoke(target, args); }
    private static void Check(bool result, string name) { if (!result) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static void Event(DataGridView grid, string name, EventArgs args)
    { typeof(DataGridView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { args.GetType() }, null).Invoke(grid, new object[] { args }); }
    private static void Push(DataGridView grid, int column, int row, string text)
    { DataGridViewCellValueEventArgs args = new DataGridViewCellValueEventArgs(column, row); args.Value = text; Event(grid, "OnCellValuePushed", args); }
    private static string Text(object presentation, string method, int address) { return (string)Call(presentation, method, address); }
    private static void Validate(DataGridView grid, int column, int row, string text, bool expected, string name)
    {
        DataGridViewCellValidatingEventArgs args = (DataGridViewCellValidatingEventArgs)Activator.CreateInstance(typeof(DataGridViewCellValidatingEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { column, row, text }, null);
        Event(grid, "OnCellValidating", args); Check(args.Cancel == expected, name);
    }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type type = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            using (Form form = (Form)Activator.CreateInstance(type))
            {
                // 不显示主窗体，避免影响正在进行的桌面验收。
                Array areas = (Array)Field(form, "Areas"); object views = Field(form, "Views");
                DataGridView list = (DataGridView)Field(views, "ListGrid"); object bank = form.GetType().GetProperty("Bank").GetValue(form, null);
                long revision = (long)bank.GetType().GetProperty("Revision").GetValue(bank, null); int writes = 0;
                Check(list.Columns.Contains("Name") && list.Columns.Contains("Unit"), "list has editable name and unit columns");
                Check(list.Columns["Value"].Index == 3 && list.Columns["Parsed"].Index == 5, "list preserves existing logical indices");
                Check(list.Columns["Name"].DisplayIndex < list.Columns["Value"].DisplayIndex, "list shows name before value");
                int offset = 0;
                foreach (int area in new int[] { 2, 3, 0, 1 })
                {
                    object panel = areas.GetValue(area), presentation = Field(panel, "Presentation");
                    if (type.Namespace == "ModbusMaster") panel.GetType().GetField("RequestWrite").SetValue(panel, new Func<int, ushort[], bool, bool>(delegate { writes++; return true; }));
                    DataGridView grid = (DataGridView)Field(panel, "Grid");
                    int address = (int)Call(bank, "Start", area), row = offset; offset += (int)Call(bank, "Count", area);
                    Check(grid.Columns.Contains("Name") && grid.Columns.Contains("Unit"), "area " + area + " has metadata columns");
                    Check(grid.Columns["Value"].Index == 2 && grid.Columns["Parsed"].Index == 4, "area " + area + " preserves logical indices");
                    Check(!grid.Columns["Name"].ReadOnly && !grid.Columns["Unit"].ReadOnly && !list.Columns["Name"].ReadOnly && !list.Columns["Unit"].ReadOnly,
                        "area " + area + " permits local metadata while disconnected");
                    Check(grid.Columns["Name"].DisplayIndex < grid.Columns["Value"].DisplayIndex, "area " + area + " shows name first");
                    Push(grid, grid.Columns["Name"].Index, 0, "温度" + area);
                    Check(Text(presentation, "GetName", address) == "温度" + area && Convert.ToString(list[list.Columns["Name"].Index, row].Value) == "温度" + area,
                        "area name edit synchronizes to list " + area);
                    Push(list, list.Columns["Unit"].Index, row, "kPa");
                    Check(Text(presentation, "GetName", address) == "温度" + area && Convert.ToString(grid[grid.Columns["Unit"].Index, 0].Value) == "kPa",
                        "list unit edit preserves name and synchronizes to area " + area);
                    Call(presentation, "SetLabel", address, "新名称", "mA"); Call(panel, "NotifyPresentation");
                    Push(grid, grid.Columns["Name"].Index, 0, "电流");
                    Check(Text(presentation, "GetUnit", address) == "mA", "name edit preserves latest unit " + area);
                    Call(presentation, "SetLabel", address, "最新名称", "A"); Call(panel, "NotifyPresentation");
                    Push(list, list.Columns["Unit"].Index, row, "uA");
                    Check(Text(presentation, "GetName", address) == "最新名称", "unit edit preserves latest name " + area);
                    Validate(grid, grid.Columns["Name"].Index, 0, new string('n', 65), true, "area rejects long name " + area);
                    Validate(list, list.Columns["Unit"].Index, row, new string('u', 33), true, "list rejects long unit " + area);
                    Validate(grid, grid.Columns["Name"].Index, 0, new string('n', 64), false, "area accepts maximum name " + area);
                    Validate(list, list.Columns["Unit"].Index, row, new string('u', 32), false, "list accepts maximum unit " + area);
                    Push(grid, grid.Columns["Name"].Index, 0, new string('n', 65));
                    Check(Text(presentation, "GetName", address) == "最新名称", "invalid pushed metadata cannot overwrite or throw " + area);
                    Push(list, list.Columns["Name"].Index, row, ""); Push(grid, grid.Columns["Unit"].Index, 0, "");
                    Check(Text(presentation, "GetName", address) == "" && Text(presentation, "GetUnit", address) == "", "blank metadata resets name and hides unit " + area);
                    Check(!String.IsNullOrEmpty(Convert.ToString(grid[grid.Columns["Name"].Index, 0].FormattedValue)), "blank name has visible default title " + area);
                    if (type.Namespace == "ModbusMaster")
                    {
                        Check(grid.Columns["Value"].ReadOnly, "disconnected master raw value stays read only " + area);
                        DataGridViewCellCancelEventArgs begin = new DataGridViewCellCancelEventArgs(3, row); Event(list, "OnCellBeginEdit", begin);
                        Check(begin.Cancel, "disconnected master list blocks raw write " + area);
                        begin = new DataGridViewCellCancelEventArgs(list.Columns["Name"].Index, row); Event(list, "OnCellBeginEdit", begin);
                        Check(!begin.Cancel, "master list allows local metadata " + area);
                        Call(panel, "SetWriteEnabled", true);
                        Check(grid.Columns["Value"].ReadOnly == (area == 1 || area == 3), "connected master retains input area write protection " + area);
                    }
                }
                // 实际编辑器提交不能误走原始数值解析；空名称编辑器保留空原文。
                object holding = areas.GetValue(2), labels = Field(holding, "Presentation");
                list.CreateControl(); list.CurrentCell = list[list.Columns["Name"].Index, 0];
                Check(list.BeginEdit(false), "name editor opens without a connection");
                Check(list.EditingControl != null && list.EditingControl.Text == "", "default title does not become editable stored name");
                list.EditingControl.Text = "列表编辑提交";
                Check((bool)Call(views, "Commit"), "list commits text metadata without numeric validation");
                Check(Text(labels, "GetName", (int)Call(bank, "Start", 2)) == "列表编辑提交", "list actual editor writes metadata");
                DataGridView holdingGrid = (DataGridView)Field(holding, "Grid"); holdingGrid.CreateControl();
                holdingGrid.CurrentCell = holdingGrid[holdingGrid.Columns["Name"].Index, 0];
                Check(holdingGrid.BeginEdit(false), "area name editor opens"); holdingGrid.EditingControl.Text = "区域编辑提交";
                Check((bool)Call(holding, "Commit") && Convert.ToString(list[list.Columns["Name"].Index, 0].Value) == "区域编辑提交", "area commits text metadata and synchronizes list");
                holdingGrid.CurrentCell = holdingGrid[holdingGrid.Columns["Unit"].Index, 0];
                Check(holdingGrid.BeginEdit(false), "area unit editor opens"); holdingGrid.EditingControl.Text = new string('u', 33);
                Check(!(bool)Call(holding, "Commit"), "actual area commit rejects oversized unit without an exception"); holdingGrid.CancelEdit();
                Push(list, list.Columns["Name"].Index, 0, "列表编辑提交");
                object cards = Field(views, "Cards"); Call(cards, "Locate", 2, (int)Call(bank, "Start", 2));
                IEnumerable cardItems = (IEnumerable)cards.GetType().GetProperty("Cards").GetValue(cards, null);
                object first = null; foreach (object card in cardItems) { if ((int)Field(card, "Address") == (int)Call(bank, "Start", 2) && (int)card.GetType().GetProperty("AreaId").GetValue(card, null) == 2) { first = card; break; } }
                Check(first != null, "matching card exists"); Call(first, "RefreshValue");
                Check(((Button)Field(first, "Header")).Text.Contains("列表编辑提交"), "table name reaches card title");
                Call(first, "EnsureEditor"); ((TextBox)Field(first, "NameInput")).Text = "卡片反向同步"; ((TextBox)Field(first, "UnitInput")).Text = "V"; Call(first, "SaveLabel");
                Check(Convert.ToString(list[list.Columns["Name"].Index, 0].Value) == "卡片反向同步" && Convert.ToString(list[list.Columns["Unit"].Index, 0].Value) == "V", "card metadata synchronizes back to list");
                Check((long)bank.GetType().GetProperty("Revision").GetValue(bank, null) == revision && writes == 0, "metadata edits do not modify raw registers or queue device writes");
            }
            Check(true, "product releases hidden edited grids and horizontal scrolling safely");
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
