using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class RawSendMemoryChecks
{
    private static int checks;
    private static object Field(object source, string name)
    { return source.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(source); }
    private static object Call(object source, string name, params object[] args)
    { return source.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(source, args); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static Form Create(Type type, string path, Action onSend)
    {
        Form form = (Form)Activator.CreateInstance(type, new object[] { path }); ((System.Windows.Forms.Timer)Field(form, "timer")).Stop();
        Control raw = (Control)Field(form, "rawSender"); raw.Enabled = true;
        raw.GetType().GetProperty("GetTargets").SetValue(raw, new Func<string[]>(delegate { return new string[] { "isolated-test-peer" }; }), null);
        raw.GetType().GetProperty("SendRequested").SetValue(raw, new Func<string, byte[], bool>(delegate { onSend(); return true; }), null);
        raw.GetType().GetProperty("IsSendBusy").SetValue(raw, new Func<bool>(delegate { return false; }), null);
        Call(raw, "RefreshTargets"); form.Show(); Application.DoEvents(); return form;
    }
    private static void Set(Control raw, string text, bool hex, int checksum, int byteOrder, int newline, int period)
    {
        ((TextBox)Field(raw, "InputBox")).Text = text; ((RadioButton)Field(raw, "HexRadio")).Checked = hex; ((RadioButton)Field(raw, "AsciiRadio")).Checked = !hex;
        ((ComboBox)Field(raw, "ChecksumCombo")).SelectedIndex = checksum; ((ComboBox)Field(raw, "ByteOrderCombo")).SelectedIndex = byteOrder;
        ((ComboBox)Field(raw, "NewlineCombo")).SelectedIndex = newline; ((NumericUpDown)Field(raw, "PeriodInput")).Value = period;
    }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1"); Application.EnableVisualStyles();
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0])); Type type = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            string directory = Path.Combine(Path.GetFullPath(args[1]), "RawSendMemory-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string[] badTypes = { "{\"Version\":1,\"RawPeriod\":2147483648}", "{\"Version\":1,\"RawPeriod\":\"bogus\"}", "{\"Version\":1,\"RawHex\":\"bogus\"}", "{\"Version\":1,\"RawText\":{\"a\":1}}" };
            for (int i = 0; i < badTypes.Length; i++)
            {
                string badPath = Path.Combine(directory, "bad-type" + i + ".json"); File.WriteAllText(badPath, badTypes[i]);
                object result = assembly.GetType(type.Namespace + ".UiLayoutState").GetMethod("Load").Invoke(null, new object[] { badPath });
                Check(result == null, "malformed field types and integer overflow safely fall back " + i);
            }
            string[] drafts = { "01 03 00 00 00 01", "HELLO\r\nSecond line", "0x1 unfinished 中文草稿", new string('A', 131072), new string('中', 131072), "" };
            int totalSent = 0;
            for (int i = 0; i < drafts.Length; i++)
            {
                string path = Path.Combine(directory, "case" + i + ".json"); bool hex = i % 2 == 0; int checksum = new int[] { 12, 4, 13, 9, 0, 2 }[i];
                int order = i % 2, newline = i % 4, period = new int[] { 20, 975, 600000, 12345, 1000, 250 }[i];
                using (Form form = Create(type, path, delegate { totalSent++; })) { Set((Control)Field(form, "rawSender"), drafts[i], hex, checksum, order, newline, period); form.Close(); }
                Check(File.Exists(path), "normal close saves custom sender case " + i);
                using (Form reopened = Create(type, path, delegate { totalSent++; }))
                {
                    Control raw = (Control)Field(reopened, "rawSender");
                    Check(((TextBox)Field(raw, "InputBox")).Text == drafts[i], "draft is restored verbatim including multiline or incomplete input " + i);
                    Check(((RadioButton)Field(raw, "HexRadio")).Checked == hex && ((RadioButton)Field(raw, "AsciiRadio")).Checked == !hex, "HEX/ASCII restored " + i);
                    Check(((ComboBox)Field(raw, "ChecksumCombo")).SelectedIndex == checksum && ((ComboBox)Field(raw, "ByteOrderCombo")).SelectedIndex == order && ((ComboBox)Field(raw, "NewlineCombo")).SelectedIndex == newline, "checksum byte order and newline restored " + i);
                    Check(((NumericUpDown)Field(raw, "PeriodInput")).Value == period, "cycle interval restored " + i);
                    Check(!((CheckBox)Field(raw, "CycleCheck")).Checked && !(bool)raw.GetType().GetProperty("IsCycling").GetValue(raw, null), "reopen never enables cycle " + i);
                    reopened.Close();
                }
            }
            Check(totalSent == 0, "restoring drafts and settings never calls the send backend");
            string active = Path.Combine(directory, "active.json");
            using (Form form = Create(type, active, delegate { totalSent++; }))
            {
                Control raw = (Control)Field(form, "rawSender"); Set(raw, "01 03", true, 1, 1, 3, 1000);
                ((CheckBox)Field(raw, "CycleCheck")).Checked = true; Check(totalSent == 1, "explicit cycle start sends before close in isolated fixture"); form.Close();
            }
            using (Form reopened = Create(type, active, delegate { totalSent++; }))
            {
                DateTime until = DateTime.UtcNow.AddMilliseconds(100); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
                Control raw = (Control)Field(reopened, "rawSender"); Check(totalSent == 1 && !((CheckBox)Field(raw, "CycleCheck")).Checked, "closing while cycling restores settings but not cycle execution");
                Check(((TextBox)Field(raw, "InputBox")).Text == "01 03" && ((ComboBox)Field(raw, "ChecksumCombo")).SelectedIndex == 1, "active cycle close retains its packet settings"); reopened.Close();
            }
            string legacy = Path.Combine(directory, "legacy.json"); File.WriteAllText(legacy, "{\"Version\":1,\"SidebarCollapsed\":true,\"LogHeight\":132}");
            using (Form form = Create(type, legacy, delegate { totalSent++; }))
            {
                Control raw = (Control)Field(form, "rawSender"); Check(((SplitContainer)Field(form, "BodySplit")).Panel1Collapsed && ((RadioButton)Field(raw, "HexRadio")).Checked && ((ComboBox)Field(raw, "ChecksumCombo")).SelectedIndex == 0 && ((NumericUpDown)Field(raw, "PeriodInput")).Value == 1000, "old layout files retain layout and original sender defaults"); form.Close();
            }
            string invalid = Path.Combine(directory, "invalid.json"); File.WriteAllText(invalid, "{\"Version\":1,\"RawText\":\"01\",\"RawChecksum\":999,\"RawByteOrder\":-5,\"RawNewline\":999,\"RawPeriod\":-1,\"CycleCheck\":true}");
            using (Form form = Create(type, invalid, delegate { totalSent++; }))
            {
                Control raw = (Control)Field(form, "rawSender"); Check(((ComboBox)Field(raw, "ChecksumCombo")).SelectedIndex == 0 && ((ComboBox)Field(raw, "ByteOrderCombo")).SelectedIndex == 0 && ((ComboBox)Field(raw, "NewlineCombo")).SelectedIndex == 0 && ((NumericUpDown)Field(raw, "PeriodInput")).Value == 1000 && !((CheckBox)Field(raw, "CycleCheck")).Checked, "invalid settings fall back safely and unknown auto-start flag is ignored"); form.Close();
            }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
