using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;

// 引用实际发行程序集，检查窄侧栏的可读性与原有设置行为；不创建服务或打开串口。
internal static class Settings24Checks
{
    private static int checks;
    private static void Check(bool condition, string text)
    { if (!condition) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
    private static T Field<T>(object source, string name)
    { return (T)source.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(source); }
    private static List<Control> Children(Control root)
    {
        List<Control> result = new List<Control>();
        foreach (Control child in root.Controls) { result.Add(child); result.AddRange(Children(child)); }
        return result;
    }
    private static Button ButtonWith(Control root, string text)
    { foreach (Control child in Children(root)) if (child is Button && child.Text == text) return (Button)child; return null; }
    private static Rectangle BoundsIn(Control root, Control child)
    { return new Rectangle(root.PointToClient(child.Parent.PointToScreen(child.Location)), child.Size); }
    private static void Below(Control root, Control upper, Control lower, string text)
    { Check(BoundsIn(root, upper).Bottom <= BoundsIn(root, lower).Top, text); }
    private static void VerifyArea(int area, int width, string output)
    {
        RegisterBank bank = new RegisterBank(); bank.Configure(area, 0, 10);
        using (AreaPanel panel = new AreaPanel(bank, area, delegate { }))
        using (Form host = new Form())
        {
            host.Font = new Font("Microsoft YaHei UI", 9);
            host.ClientSize = new Size(width, 410); host.Controls.Add(panel.Settings); host.Show(); Application.DoEvents();
            Control settings = panel.Settings;
            Button apply = ButtonWith(settings, "应用数据区定义"), same = ButtonWith(settings, "常数填充"), increment = ButtonWith(settings, "递增填充"), locate = ButtonWith(settings, "定位");
            NumericUpDown jump = Field<NumericUpDown>(panel, "jump"); TextBox fill = Field<TextBox>(panel, "fill");
            Check(apply != null && same != null && increment != null && locate != null, "all setting actions remain available " + area + "/" + width);
            // 相同一行的范围输入会在窄侧栏挤压标签；两项须独立上下排列。
            Below(settings, panel.StartInput, panel.CountInput, "start and quantity use separate vertical rows " + area + "/" + width);
            Below(settings, panel.CountInput, apply, "apply follows quantity on its own row " + area + "/" + width);
            Below(settings, apply, panel.Format, "raw format follows range action " + area + "/" + width);
            Below(settings, panel.Format, fill, "batch initial value has a separate row " + area + "/" + width);
            Below(settings, fill, same, "fill actions follow batch initial value " + area + "/" + width);
            Check(BoundsIn(settings, same).Top == BoundsIn(settings, increment).Top && BoundsIn(settings, same).Right <= BoundsIn(settings, increment).Left,
                "constant and increment share an unclipped row " + area + "/" + width);
            Below(settings, same, jump, "jump address follows fill actions " + area + "/" + width);
            Below(settings, jump, locate, "locate has a separate row below jump address " + area + "/" + width);
            Check(panel.StartInput.Width >= 100 && panel.CountInput.Width >= 100 && jump.Width >= 100,
                "address and quantity inputs have readable widths " + area + "/" + width);
            foreach (Control child in Children(settings))
            {
                if (child is Label)
                {
                    Size text = TextRenderer.MeasureText(child.Text, child.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    Check(child.Text.IndexOf('\n') < 0 && text.Width <= child.ClientSize.Width && text.Height <= child.ClientSize.Height,
                        "setting label fits a single line: " + child.Text + " " + area + "/" + width);
                }
                if (child is Button || child is NumericUpDown || child is ComboBox || child is TextBox)
                {
                    Rectangle visible = BoundsIn(settings, child);
                    Check(visible.Left >= 0 && visible.Right <= settings.ClientSize.Width && visible.Top >= 0 && visible.Bottom <= settings.ClientSize.Height,
                        "setting control remains inside sidebar: " + child.GetType().Name + " " + area + "/" + width);
                }
            }
            Button parse = ButtonWith(settings, "整区默认解析设置…");
            if (area < 2) Check(parse == null, "bit area has no parsing action " + area);
            else { Check(parse != null, "register area keeps default parsing action " + area); Below(settings, locate, parse, "default parsing follows locate " + area); }
            panel.ApplyDefinition(0, 0);
            Check(panel.CountInput.Minimum == 0 && panel.CountInput.Value == 0 && bank.Count(area) == 0 && panel.Grid.RowCount == 0,
                "zero quantity still disables area " + area + "/" + width);
            panel.ApplyDefinition(100, 2); panel.FillValues(area < 2 ? (ushort)1 : (ushort)123, false);
            Check(bank.Get(area, 100) == (area < 2 ? 1 : 123), "range and fill still update shared bank " + area + "/" + width);
            panel.SetRunning(true); Check(!panel.StartInput.Enabled && !panel.CountInput.Enabled && !apply.Enabled, "active service locks range definition " + area);
            panel.SetRunning(false); Check(panel.StartInput.Enabled && panel.CountInput.Enabled && apply.Enabled, "stopping service restores range editing " + area);
            if (!String.IsNullOrEmpty(output) && area == 2 && width == 240)
                using (Bitmap bitmap = new Bitmap(host.ClientSize.Width, host.ClientSize.Height))
                { host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "2.4-设置纵向布局.png")); }
            host.Close();
        }
    }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string output = args.Length > 0 ? args[0] : null;
            foreach (int width in new int[] { 240, 300 }) for (int area = 0; area < 4; area++) VerifyArea(area, width, output);
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
