using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// 在实际发行程序上验证主题；不启动连接，不改变原始寄存器、元数据或发送报文。
internal static class UiThemeChecks
{
    private static int checks;
    private static object Field(object source, string name)
    { return source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(source); }
    private static object Call(object source, string name, params object[] args)
    { return source.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(source, args); }
    private static void Check(bool value, string text)
    { if (!value) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
    private static void Pump() { Application.DoEvents(); Application.DoEvents(); }
    private static void Capture(Form form, string directory, string name)
    { if (String.IsNullOrEmpty(directory)) return; Directory.CreateDirectory(directory); using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, name + ".png")); } }
    private static double Luminance(Color color)
    { double total = 0; double[] values = { color.R / 255.0, color.G / 255.0, color.B / 255.0 }; double[] factors = { .2126, .7152, .0722 }; for (int i = 0; i < 3; i++) total += factors[i] * (values[i] <= .04045 ? values[i] / 12.92 : Math.Pow((values[i] + .055) / 1.055, 2.4)); return total; }
    private static double Contrast(Color a, Color b)
    { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type main = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            Type theme = assembly.GetType(main.Namespace + ".UiTheme");
            Check(theme != null, "shared theme API exists");
            MethodInfo setTheme = theme.GetMethod("SetTheme"), apply = theme.GetMethod("Apply");
            setTheme.Invoke(null, new object[] { 0 });
            using (Form form = (Form)Activator.CreateInstance(main))
            {
                form.Show(); Pump(); apply.Invoke(null, new object[] { form });
                RichTextBox log = (RichTextBox)Field(form, "logs"); object sender = Field(form, "rawSender");
                TextBox input = (TextBox)Field(sender, "InputBox"); input.Text = "01 03 00 00 00 02";
                string rx = "12:00:00.000 RX: 01 03 04 00 01 00 02", tx = "12:00:00.001 TX: 01 03 00 00 00 02";
                Call(log, "Clear"); Call(log, "AppendLines", new List<string> { rx, tx, "12:00:00.002 错误 error" });
                log.Select(3, 9); string selected = log.SelectedText, logText = log.Text;
                object views = Field(form, "Views"), bank = main.GetProperty("Bank").GetValue(form, null);
                object value = Call(bank, "Get", 2, 0); DataGridView grid = (DataGridView)Field(views, "ListGrid");
                object presentation = Field(((Array)Field(form, "Areas")).GetValue(2), "Presentation");
                string originalName = (string)Call(presentation, "GetName", 0), originalUnit = (string)Call(presentation, "GetUnit", 0);
                object originalRule = Call(presentation, "GetRule", 0);
                Color dayBack = form.BackColor, dayHeader = grid.ColumnHeadersDefaultCellStyle.BackColor, dayLog = log.BackColor;
                Font font = input.Font; object marker = new object(); input.Tag = marker;
                for (int turn = 0; turn < 3; turn++)
                {
                    ((ComboBox)Field(form, "themeMode")).SelectedIndex = 1; Pump();
                    Check((int)theme.GetProperty("Current").GetValue(null, null) == 1 && form.BackColor.GetBrightness() < .25f, "footer immediately changes the active theme");
                    setTheme.Invoke(null, new object[] { 1 }); apply.Invoke(null, new object[] { form }); Pump();
                    Check(form.BackColor.GetBrightness() < .25f && input.BackColor.GetBrightness() < .3f, "night colors cover form and raw input");
                    Check(Contrast(input.ForeColor, input.BackColor) >= 4.5, "night input text has readable contrast");
                    Check(!grid.EnableHeadersVisualStyles && grid.ColumnHeadersDefaultCellStyle.BackColor.GetBrightness() < .35f && grid.AlternatingRowsDefaultCellStyle.BackColor != grid.DefaultCellStyle.BackColor, "night grid headers and alternate rows are themed");
                    Check(Contrast(grid.DefaultCellStyle.SelectionForeColor, grid.DefaultCellStyle.SelectionBackColor) >= 4.5, "night selected rows have readable contrast");
                    Check(log.Text == logText && log.SelectedText == selected, "recolor preserves log records and selection");
                    log.Select(0, 1); Color rxColor = log.SelectionColor; log.Select(rx.Length + 1, 1); Color txColor = log.SelectionColor;
                    Check(rxColor != txColor && Contrast(rxColor, log.BackColor) >= 4.5 && Contrast(txColor, log.BackColor) >= 4.5, "night RX and TX remain distinct and readable"); log.Select(3, 9);
                    Check(input.Text == "01 03 00 00 00 02" && ReferenceEquals(input.Tag, marker) && ReferenceEquals(input.Font, font) && Equals(Call(bank, "Get", 2, 0), value), "theme preserves input, register, tag and font");
                    Check((string)Call(presentation, "GetName", 0) == originalName && (string)Call(presentation, "GetUnit", 0) == originalUnit && ReferenceEquals(Call(presentation, "GetRule", 0), originalRule), "theme preserves names units and parsing rules");
                    NumericUpDown number = (NumericUpDown)Field(form, "unit"); decimal numericValue = number.Value; number.Enabled = false;
                    Check(number.BackColor.GetBrightness() < .2f && number.Value == numericValue, "disabled numeric input retains dark background and value"); number.Enabled = true;
                    input.ReadOnly = true; Check(input.BackColor.GetBrightness() < .3f, "read-only raw sender retains dark background"); input.ReadOnly = false;
                    if (turn == 0) Capture(form, args.Length > 1 ? args[1] : null, main.Namespace + "-night-list");
                    using (Panel dynamic = new Panel())
                    {
                        TextBox child = new TextBox { Text = "dynamic" }; dynamic.Controls.Add(child); form.Controls.Add(dynamic);
                        Check(child.BackColor.GetBrightness() < .3f && child.Text == "dynamic", "ControlAdded themes new input without polling"); form.Controls.Remove(dynamic);
                    }
                    ((ComboBox)Field(form, "displayMode")).SelectedIndex = 4; Pump();
                    object cards = Field(views, "Cards"); Call(cards, "Locate", 2, 0); Pump();
                    foreach (object card in (System.Collections.IEnumerable)cards.GetType().GetProperty("Cards").GetValue(cards, null))
                    {
                        Check(((Control)card).BackColor.GetBrightness() < .3f, "dynamic cards use night surface"); Call(card, "RefreshValue");
                        Label label = (Label)Field(card, "ValueLabel"); if (label.Visible) Check(Contrast(label.ForeColor, label.BackColor) >= 4.5, "refreshed card values retain night contrast");
                        break;
                    }
                    if (turn == 0) Capture(form, args.Length > 1 ? args[1] : null, main.Namespace + "-night-cards");
                    Type rule = assembly.GetType(main.Namespace + ".ParseRule");
                    using (Form dialog = (Form)Activator.CreateInstance(assembly.GetType(main.Namespace + ".RuleEditor"), new object[] { Activator.CreateInstance(rule), "theme test" }))
                        Check(dialog.BackColor.GetBrightness() < .25f, "new rule dialog inherits active theme");
                    ((ComboBox)Field(form, "displayMode")).SelectedIndex = 3; Pump();
                    Array floating = (Array)Field(views, "floats"); int windows = 0;
                    foreach (Form window in floating) if (window != null)
                    { Check(window.BackColor.GetBrightness() < .25f && !window.TopMost, "new floating window inherits night theme without topmost"); windows++; }
                    Check(windows > 0, "floating views created for theme verification");
                    ((ComboBox)Field(form, "themeMode")).SelectedIndex = 0; Pump();
                    setTheme.Invoke(null, new object[] { 0 }); apply.Invoke(null, new object[] { form }); Pump();
                    foreach (Form window in floating) if (window != null) Check(window.BackColor.GetBrightness() > .5f, "theme updates existing detached floating windows");
                    ((ComboBox)Field(form, "displayMode")).SelectedIndex = 0; Pump();
                    Check(form.BackColor == dayBack && grid.ColumnHeadersDefaultCellStyle.BackColor == dayHeader && log.BackColor == dayLog, "day mode restores original form grid and log palette");
                    if (turn == 0) Capture(form, args.Length > 1 ? args[1] : null, main.Namespace + "-day-restored");
                }
                form.Close();
            }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
