using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class CompactUi28Checks
{
    private static int checks;
    private static object Field(object source, string name)
    { FieldInfo field = source.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); return field == null ? null : field.GetValue(source); }
    private static object Call(object source, string name, params object[] args)
    { return source.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(source, args); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Pump() { Application.DoEvents(); Application.DoEvents(); }
    private static bool FitsAncestors(Control control)
    {
        Rectangle bounds = new Rectangle(control.Parent.PointToScreen(control.Location), control.Size);
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds)) return false;
        return true;
    }
    private static void CheckHeaderBounds(Button logToggle, Button rawToggle)
    {
        Check(FitsAncestors(logToggle) && FitsAncestors(rawToggle), "scaled section captions fit all ancestor client bounds");
        foreach (Control child in logToggle.Parent.Controls[1].Controls)
            if (child.Visible) Check(FitsAncestors(child), "scaled log action fits header: " + child.Text);
    }
    private static void Capture(Form form, string path)
    { using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path); } }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type formType = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            using (Form form = (Form)Activator.CreateInstance(formType))
            {
                form.Show(); Pump();
                RichTextBox log = Field(form, "logs") as RichTextBox;
                Check(log != null && log.WordWrap && log.ReadOnly, "long traffic records use native automatic wrapping");
                Check(assembly.GetName().Version == new Version(2, 9, 0, 0) && form.Text.Contains("2.9"), "assembly and title share version 2.8");
                Control view = (Control)Field(form, "Views");
                Check(view.PointToScreen(Point.Empty).Y - form.PointToScreen(Point.Empty).Y < 65, "large brand/title row removed");
                Button logToggle = Field(form, "LogToggle") as Button, rawToggle = Field(form, "RawSendToggle") as Button;
                Check(logToggle != null && rawToggle != null && logToggle.Visible && rawToggle.Visible, "both independent collapse controls remain available");
                Check(logToggle.Text.Contains("通信日志") && logToggle.Height >= logToggle.Font.Height + 4 && logToggle.Bottom <= logToggle.Parent.ClientSize.Height, "log collapse caption fits fully inside its header");
                Control sender = (Control)Field(form, "rawSender"); TextBox input = (TextBox)Field(sender, "InputBox"); input.Text = "01 03 00 00 00 01";
                CheckBox pause = (CheckBox)Field(form, "pause"); pause.Checked = true;
                Call(log, "Clear");
                string rx = "12:00:00.000 RX: " + new string('A', 1600), tx = "12:00:00.001 TX: 01 03 00 00";
                Call(log, "AppendLines", new List<string> { rx, tx }); Pump();
                Check(log.GetLineFromCharIndex(rx.Length - 1) > log.GetLineFromCharIndex(0), "a long HEX record wraps onto multiple visual rows");
                string copied = (string)Call(log, "CopyText", true);
                Check(copied == rx + Environment.NewLine + tx + Environment.NewLine, "automatic wrapping does not insert export/copy line breaks");
                log.Select(0, 1); Color rxColor = log.SelectionColor; log.Select(rx.Length + 1, 1); Color txColor = log.SelectionColor;
                Check(rxColor != txColor && rxColor.B > rxColor.R && txColor.G > txColor.R, "wrapped RX and TX retain distinct colors");
                log.Select(4, 9); string selected = log.SelectedText;
                Call(log, "AppendLines", new List<string> { "12:00:01.000 TX: appended" });
                Check(log.SelectedText == selected && (string)Call(log, "CopyText", false) == selected, "incoming records preserve selected text and selection copy"); log.Select(log.TextLength, 0);
                int initialHeight = view.Height;
                rawToggle.PerformClick(); Pump();
                Check(!sender.Visible && log.Visible && view.Height > initialHeight + 85, "collapsing raw sender returns space to data view");
                int rawHiddenHeight = view.Height; logToggle.PerformClick(); Pump();
                Check(!log.Visible && !sender.Visible && view.Height > rawHiddenHeight + 35, "collapsing both returns log space and keeps restore buttons");
                pause.Checked = false; Call(form, "AddLog", "RX: hidden-log-marker"); Call(form, "TickUi");
                Check(log.Text.Contains("hidden-log-marker"), "collapsed log keeps receiving records");
                rawToggle.PerformClick(); Pump();
                Check(sender.Visible && !log.Visible && input.Text == "01 03 00 00 00 01", "raw sender independently restores draft with log hidden");
                logToggle.PerformClick(); Pump();
                Check(sender.Visible && log.Visible && log.Text.Contains(rx), "expanding restores both contents");
                for (int i = 0; i < 8; i++) { rawToggle.PerformClick(); logToggle.PerformClick(); Pump(); }
                form.Size = form.MinimumSize; Pump();
                Check(sender.Visible && log.Visible && input.Height >= 40 && log.Height >= 45 && sender.Bottom <= sender.Parent.ClientSize.Height, "minimum window keeps logs and sender usable after repeated toggles");
                form.Scale(new SizeF(1.25f, 1.25f)); Pump(); rawToggle.PerformClick(); logToggle.PerformClick(); Pump(); rawToggle.PerformClick(); logToggle.PerformClick(); Pump();
                Check(sender.Visible && log.Visible && logToggle.Visible && rawToggle.Visible, "scaled collapse controls restore both sections");
                CheckHeaderBounds(logToggle, rawToggle);
                form.Scale(new SizeF(1.25f, 1.25f)); Pump(); CheckHeaderBounds(logToggle, rawToggle);
                form.Scale(new SizeF(0.8f, 0.8f)); Pump();
                Call(log, "Clear"); List<string> records = new List<string>(); for (int i = 0; i < 1002; i++) records.Add("RX: record-" + i);
                Call(log, "AppendLines", records);
                Check((int)log.GetType().GetProperty("RecordCount").GetValue(log, null) == 1000 && !log.Text.StartsWith("RX: record-0\n") && log.Text.Contains("RX: record-1001"), "log remains bounded to the latest 1000 whole records");
                form.Scale(new SizeF(0.8f, 0.8f)); form.Size = new Size(1240, 860); Pump();
                Call(log, "Clear"); Call(log, "AppendLines", new List<string> { rx, tx, "12:00:00.002 就绪：自动换行保留完整报文" });
                ((ComboBox)Field(form, "displayMode")).SelectedIndex = 4; Pump();
                if (args.Length > 1) { Directory.CreateDirectory(args[1]); Capture(form, Path.Combine(args[1], formType.Namespace + "-2.8紧凑布局.png")); logToggle.PerformClick(); rawToggle.PerformClick(); Pump(); Capture(form, Path.Combine(args[1], formType.Namespace + "-2.8折叠布局.png")); }
                form.Close();
            }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
