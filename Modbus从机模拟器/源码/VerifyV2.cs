using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ModbusSimulator;

internal static class VerifyV2
{
    private static int passed;
    private static byte[] Hex(string text) { return Array.ConvertAll(text.Split(' '), s => Convert.ToByte(s, 16)); }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static byte[] Exact(NetworkStream stream, int count)
    {
        byte[] bytes = new byte[count]; int offset = 0;
        while (offset < count) { int n = stream.Read(bytes, offset, count - offset); if (n == 0) throw new Exception("EOF"); offset += n; } return bytes;
    }
    private static void Exchange(TcpClient client, string request, string expected)
    {
        byte[] bytes = Hex(request); NetworkStream stream = client.GetStream(); stream.Write(bytes, 0, bytes.Length);
        byte[] header = Exact(stream, 6), body = Exact(stream, RegisterBank.Word(header, 4));
        byte[] reply = new byte[6 + body.Length]; Buffer.BlockCopy(header, 0, reply, 0, 6); Buffer.BlockCopy(body, 0, reply, 6, body.Length);
        Check(BitConverter.ToString(reply).Replace('-', ' ') == expected, request + " -> " + expected);
    }
    private static T Field<T>(object source, string name)
    { return (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source); }
    private static void Edit(AreaPanel panel, int row, string text)
    {
        panel.Grid.CurrentCell = panel.Grid[2, row]; Check(panel.Grid.BeginEdit(true), "begin edit area " + panel.Area);
        ((TextBox)panel.Grid.EditingControl).Text = text; Check(panel.Grid.EndEdit(), "commit edit area " + panel.Area);
    }
    private static void Capture(Form form, string path)
    { using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); } }
    private static void Pump(int ms)
    { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < end); }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm())
            {
                form.Show(); Check(form.Views.CurrentMode == 0, "default display is combined list");
                Field<ComboBox>(form, "displayMode").SelectedIndex = 1;
                Check(form.Views.CurrentMode == 1, "switch to tiled data views"); Pump(100);
                foreach (AreaPanel panel in form.Areas) Check(panel.Visible && panel.Grid.VirtualMode && panel.Grid.RowCount == 10, "four area visible and virtual: " + panel.Area);
                form.Areas[2].CountInput.Value = 2000;
                try { form.CaptureConfiguration(); throw new Exception("unapplied definition silently accepted"); } catch (ArgumentException) { }
                Check(form.Bank.Count(2) == 10, "unapplied definition prevents saving mismatched configuration");
                form.Areas[0].ApplyDefinition(10, 9); form.Areas[1].ApplyDefinition(20, 8);
                form.Areas[2].ApplyDefinition(100, 2000); form.Areas[3].ApplyDefinition(65535, 1);
                Check(form.Areas[2].Grid.RowCount == 2000, "count controls actual table size");
                Edit(form.Areas[2], 0, "0x1234"); Check(form.Bank.Get(2, 100) == 4660, "HEX edit immediate storage");
                form.Areas[2].Format.SelectedIndex = 1;
                Edit(form.Areas[2], 1, "-1"); Check(form.Bank.Get(2, 101) == 65535, "signed edit raw uint16");
                form.Areas[2].Format.SelectedIndex = 2;
                Check(Convert.ToString(form.Areas[2].Grid[2, 1].Value) == "0xFFFF", "format switches without changing bits");
                form.Areas[2].Format.SelectedIndex = 1;
                form.Areas[2].Grid.CurrentCell = form.Areas[2].Grid[2, 1]; form.Areas[2].Grid.BeginEdit(true);
                ((TextBox)form.Areas[2].Grid.EditingControl).Text = "-2";
                form.Areas[2].Format.SelectedIndex = 2;
                Check(form.Bank.Get(2, 101) == 65534 && Convert.ToString(form.Areas[2].Grid[2, 1].Value) == "0xFFFE", "format change commits using previous signed interpretation");
                Edit(form.Areas[3], 0, "4321"); Edit(form.Areas[0], 0, "1"); Edit(form.Areas[1], 0, "1");
                form.Areas[0].Grid.CurrentCell = form.Areas[0].Grid[2, 0]; form.Areas[0].Grid.BeginEdit(true);
                ((TextBox)form.Areas[0].Grid.EditingControl).Text = "2";
                bool invalidCommit = form.Areas[0].Commit();
                Check(!invalidCommit && form.Bank.Get(0, 10) == 1, "invalid bit cannot commit or corrupt storage"); form.Areas[0].Grid.CancelEdit();
                form.Areas[2].FillValues(100, true); Check(form.Bank.Get(2, 100) == 100 && form.Bank.Get(2, 2099) == 2099, "bulk increment whole configured range");
                SimulatorConfiguration snapshot = form.CaptureConfiguration();
                string configPath = Path.Combine(args[0], "UI配置回读.json"); snapshot.Save(configPath);
                form.Bank.Set(2, 100, 999); form.ApplyConfiguration(SimulatorConfiguration.Load(configPath));
                Check(form.Bank.Get(2, 100) == 100 && form.Areas[3].Grid.RowCount == 1, "UI config restore data and definitions");
                snapshot = form.CaptureConfiguration(); snapshot.Areas[3].Count = 0;
                try { form.ApplyConfiguration(snapshot); throw new Exception("bad import accepted"); } catch (ArgumentException) { }
                Check(form.Bank.Get(2, 100) == 100 && form.Bank.Count(3) == 1, "invalid UI import preserves all current state");

                // 由窗口启动真实监听，并在同一连接中交替访问全部四个数据区。
                NumericUpDown uiPort = Field<NumericUpDown>(form, "port"); uiPort.Value = 25000 + System.Diagnostics.Process.GetCurrentProcess().Id % 10000;
                Field<TextBox>(form, "ip").Text = "127.0.0.1";
                Button toggle = Field<Button>(form, "toggle"); toggle.PerformClick();
                Check(toggle.Text == "停止从机" && !form.Areas[2].StartInput.Enabled, "running locks range definitions");
                try { form.ApplyConfiguration(form.CaptureConfiguration()); throw new Exception("load during run accepted"); } catch (InvalidOperationException) { }
                using (TcpClient client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, (int)uiPort.Value); client.ReceiveTimeout = 2000;
                    Exchange(client, "00 01 00 00 00 06 01 01 00 0A 00 01", "00 01 00 00 00 04 01 01 01 01");
                    Exchange(client, "00 02 00 00 00 06 01 02 00 14 00 01", "00 02 00 00 00 04 01 02 01 01");
                    Exchange(client, "00 03 00 00 00 06 01 03 00 64 00 02", "00 03 00 00 00 07 01 03 04 00 64 00 65");
                    Exchange(client, "00 04 00 00 00 06 01 04 FF FF 00 01", "00 04 00 00 00 05 01 04 02 10 E1");
                    Exchange(client, "00 05 00 00 00 06 01 03 00 63 00 01", "00 05 00 00 00 03 01 83 02");
                    Exchange(client, "00 06 00 00 00 06 01 06 00 64 AB CD", "00 06 00 00 00 06 01 06 00 64 AB CD");
                    Exchange(client, "00 07 00 00 00 06 01 03 00 64 00 01", "00 07 00 00 00 05 01 03 02 AB CD");
                    Exchange(client, "00 08 00 00 00 0B 01 10 00 64 00 02 04 00 11 00 22", "00 08 00 00 00 06 01 10 00 64 00 02");
                    Exchange(client, "00 09 00 00 00 06 01 03 00 64 00 02", "00 09 00 00 00 07 01 03 04 00 11 00 22");
                    Exchange(client, "00 0A 00 00 00 06 01 05 00 0A 00 00", "00 0A 00 00 00 06 01 05 00 0A 00 00");
                    Exchange(client, "00 0B 00 00 00 09 01 0F 00 0A 00 09 02 AA 01", "00 0B 00 00 00 06 01 0F 00 0A 00 09");
                    Exchange(client, "00 0C 00 00 00 06 01 01 00 0A 00 09", "00 0C 00 00 00 05 01 01 02 AA 01");
                    Pump(350);
                    Check(Convert.ToString(form.Areas[2].Grid[2, 0].Value) == "0x0011", "master writes appear in virtual grid");
                    bool foundTx = Field<TrafficLog>(form, "logs").Text.Contains("已写入 TCP TID=");
                    Check(foundTx, "actual write logs carry transaction ID");
                }
                Capture(form, Path.Combine(args[0], "2.0运行界面.png"));
                toggle.PerformClick(); Check(toggle.Text == "启动从机" && form.Areas[2].StartInput.Enabled, "stop restores definition editing");
                ComboBox display = Field<ComboBox>(form, "displayMode");
                display.SelectedIndex = 0; Pump(50);
                Check(form.Views.ListGrid.RowCount == 2018, "combined list contains all configured objects");
                form.Views.Locate(3, 65535);
                Check(form.Views.ListGrid.CurrentCell.RowIndex == 2000, "unified locate jumps to chosen area within combined list");
                form.Views.ListGrid.CurrentCell = form.Views.ListGrid[3, 0]; form.Views.ListGrid.BeginEdit(true);
                ((TextBox)form.Views.ListGrid.EditingControl).Text = "0x55AA";
                Check(form.Views.Commit() && form.Bank.Get(2, 100) == 21930, "combined list edit updates shared register data");
                form.Views.ListGrid.BeginEdit(true); ((TextBox)form.Views.ListGrid.EditingControl).Text = "invalid";
                display.SelectedIndex = 3;
                Check(form.Views.CurrentMode == 0 && form.OwnedForms.Length == 0, "invalid edit blocks floating rearrange without detaching views");
                form.Views.ListGrid.CancelEdit();
                display.SelectedIndex = 2; Pump(50);
                Check(form.Views.CurrentMode == 2 && form.Areas[2].Parent is TabPage, "tabbed view hosts data panels");
                form.Views.Locate(1, 20); Check(((TabPage)form.Areas[1].Parent).Visible, "unified locate selects requested tab");
                display.SelectedIndex = 3; Pump(100);
                Check(Array.TrueForAll(Field<Form[]>(form.Views, "floats"), delegate(Form floating) { return floating != null && floating.Visible && floating.Owner == null && !floating.TopMost; }) && form.WindowState == FormWindowState.Minimized, "each active area gets an independent ordinary floating window");
                form.Areas[2].Grid.CurrentCell = form.Areas[2].Grid[2, 0]; form.Areas[2].Grid.BeginEdit(true);
                ((TextBox)form.Areas[2].Grid.EditingControl).Text = "INVALID";
                form.Areas[2].FindForm().Close();
                int otherInvalidations = 0;
                InvalidateEventHandler changed = delegate { otherInvalidations++; };
                form.Areas[0].Grid.Invalidated += changed;
                form.Bank.Set(0, 10, 1); form.Views.RefreshValues();
                Check(otherInvalidations > 0, "hidden editing float does not block other areas refreshing");
                form.Areas[0].Grid.Invalidated -= changed; form.Areas[2].Grid.CancelEdit();
                Form hidden = form.Areas[0].FindForm(); hidden.Close();
                Check(!hidden.Visible && !hidden.IsDisposed && form.Bank.Get(2, 100) == 21930, "closing float hides view and preserves data");
                form.Views.ShowFloats(); Check(hidden.Visible, "rearrange restores hidden floating view");
                display.SelectedIndex = 0; Pump(100);
                Check(Array.TrueForAll(Field<Form[]>(form.Views, "floats"), delegate(Form floating) { return floating == null; }) && form.Bank.Get(2, 100) == 21930, "docking floating views back preserves data and closes windows");
                display.SelectedIndex = 1;
                form.Areas[2].ApplyDefinition(0, 65536);
                Check(form.Areas[2].Grid.RowCount == 65536, "full address space virtual grid");
                form.Areas[2].Grid.CurrentCell = form.Areas[2].Grid[2, 65535]; form.Areas[2].Grid.FirstDisplayedScrollingRowIndex = 65535;
                Edit(form.Areas[2], 65535, "0xBEEF"); Check(form.Bank.Get(2, 65535) == 48879, "edit final virtual address");
                Pump(100); Capture(form, Path.Combine(args[0], "2.0完整地址空间.png"));
                form.Size = form.MinimumSize; Pump(100); Capture(form, Path.Combine(args[0], "2.0最小窗口.png"));
                CheckLayout(form);
                foreach (AreaPanel panel in form.Areas) Check(panel.Grid.Height >= 55 && panel.Grid.Width >= 240, "minimum size usable grid area " + panel.Area);
                SimulatorConfiguration example = SimulatorConfiguration.Load(Path.Combine(Path.GetDirectoryName(args[0]), "四区联调示例.json"));
                Check(example.Mode == 2 && example.Areas[2].Values[0] == 100 && example.Areas[3].Values[1] == 5678, "supplied serial MBAP example valid");
                form.ApplyConfiguration(example); form.ClientSize = new Size(1240, 860); display.SelectedIndex = 0; Pump(200);
                Capture(form, Path.Combine(args[0], "2.0界面预览.png"));
                display.SelectedIndex = 1; Pump(100); Capture(form, Path.Combine(args[0], "2.0平铺窗格.png"));
                display.SelectedIndex = 2; Pump(100); Capture(form, Path.Combine(args[0], "2.0选项卡.png"));
                display.SelectedIndex = 3; Pump(100);
                foreach (Form floating in Field<Form[]>(form.Views, "floats")) if (floating != null) Capture(floating, Path.Combine(args[0], "2.0浮窗-" + floating.Text.Split(' ')[0] + ".png"));
                display.SelectedIndex = 0;
                form.Close();
            }
            Console.WriteLine("TOTAL " + passed + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
    private static void CheckLayout(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (!control.Visible) continue;
            if (control is Button || control is ComboBox || control is NumericUpDown || control is DataGridView)
                Check(parent.ClientRectangle.Contains(control.Bounds), "minimum layout contains " + control.GetType().Name + " " + control.Text);
            if (!(control is DataGridView) && !(control is NumericUpDown) && !(control is ComboBox)) CheckLayout(control);
        }
    }
    private static Button FindButton(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button && child.Text == text) return (Button)child;
            Button nested = FindButton(child, text); if (nested != null) return nested;
        }
        return null;
    }
}
