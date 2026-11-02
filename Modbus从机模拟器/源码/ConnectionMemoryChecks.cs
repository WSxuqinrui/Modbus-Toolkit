using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ConnectionMemoryChecks
{
    private static int checks;
    private static object Field(object source, string name)
    { FieldInfo f = source.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); return f == null ? null : f.GetValue(source); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static Form Create(Type type, string path)
    { Form f = (Form)Activator.CreateInstance(type, new object[] { path }); ((Timer)Field(f, "timer")).Stop(); f.Show(); Application.DoEvents(); return f; }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1"); Application.EnableVisualStyles();
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0])); Type type = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm"); bool master = type.Namespace == "ModbusMaster";
            Type catalog = assembly.GetType(type.Namespace + ".TransportCatalog"), devices = assembly.GetType(type.Namespace + ".SerialDevices");
            string folder = Path.Combine(Path.GetFullPath(args[1]), "ConnectionMemory-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            foreach (string field in new string[] { "Mode", "Port", "Unit", "Baud", "DataBits", "TimeoutMs", "PollIntervalMs" })
            {
                string bad = Path.Combine(folder, "empty-" + field + ".json"); File.WriteAllText(bad, "{\"Version\":1,\"Communication\":{\"" + field + "\":\"\"}}");
                Check(assembly.GetType(type.Namespace + ".UiLayoutState").GetMethod("Load").Invoke(null, new object[] { bad }) == null, "empty numeric preference safely falls back: " + field);
            }
            int[] bauds = { 1200, 2400, 4800, 9600, 19200, 57600, 115200 };
            for (int mode = 0; mode < 7; mode++)
            {
                string path = Path.Combine(folder, "mode" + mode + ".json"); string ip = mode % 2 == 0 ? "2001:db8::42" : "192.0.2.42"; string parity = mode % 2 == 0 ? "Even" : "Odd";
                using (Form form = Create(type, path))
                {
                    type.GetMethod("SelectTransport").Invoke(form, new object[] { mode });
                    ((ComboBox)Field(form, "com")).Text = "COM277: USB-SERIAL CH340"; ((ComboBox)Field(form, "baud")).SelectedItem = bauds[mode].ToString();
                    ((ComboBox)Field(form, "parity")).SelectedItem = parity; ((ComboBox)Field(form, "stopBits")).SelectedItem = "2";
                    ((ComboBox)Field(form, "dataBits")).SelectedItem = mode == 3 ? "7" : "8";
                    ((TextBox)Field(form, "ip")).Text = ip; ((NumericUpDown)Field(form, "port")).Value = 15200 + mode; ((NumericUpDown)Field(form, "unit")).Value = 70 + mode;
                    if (master) { ((NumericUpDown)Field(form, "timeoutMs")).Value = 2700; ((NumericUpDown)Field(form, "pollInterval")).Value = 720; }
                    // 未应用的数据区草稿不应阻止独立保存通信参数。
                    Array areas = (Array)Field(form, "Areas"); ((NumericUpDown)Field(areas.GetValue(2), "CountInput")).Value = 11;
                    form.Close();
                }
                using (Form form = Create(type, path))
                {
                    int restored = (int)catalog.GetMethod("IdAt").Invoke(null, new object[] { ((ComboBox)Field(form, "mode")).SelectedIndex });
                    Check(restored == mode, "protocol ID restores independently of dropdown order " + mode);
                    Check((string)devices.GetMethod("RawPort").Invoke(null, new object[] { ((ComboBox)Field(form, "com")).Text }) == "COM277", "missing COM device retains its original port identity " + mode);
                    Check(((ComboBox)Field(form, "baud")).Text == bauds[mode].ToString() && ((ComboBox)Field(form, "parity")).Text == parity && ((ComboBox)Field(form, "stopBits")).Text == "2", "baud parity and stop bits restore " + mode);
                    Check(((ComboBox)Field(form, "dataBits")).Text == (mode == 3 ? "7" : "8"), "ASCII seven data bits restore after mode events " + mode);
                    Check(((TextBox)Field(form, "ip")).Text == ip && ((NumericUpDown)Field(form, "port")).Value == 15200 + mode && ((NumericUpDown)Field(form, "unit")).Value == 70 + mode, "IP port and station restore " + mode);
                    Check(Field(form, master ? "session" : "server") == null && !((CheckBox)Field(Field(form, "rawSender"), "CycleCheck")).Checked, "restored connection settings never create a channel or cycle " + mode);
                    if (master) Check(((NumericUpDown)Field(form, "timeoutMs")).Value == 2700 && ((NumericUpDown)Field(form, "pollInterval")).Value == 720, "master timing values restore without starting polling " + mode);
                    form.Close();
                }
            }
            string old = Path.Combine(folder, "old.json"); File.WriteAllText(old, "{\"Version\":1,\"RawText\":\"01 03\",\"SidebarCollapsed\":true}");
            using (Form form = Create(type, old))
            { Check(((NumericUpDown)Field(form, "port")).Value == 502 && ((NumericUpDown)Field(form, "unit")).Value == 1 && ((TextBox)Field(form, "ip")).Text == (master ? "127.0.0.1" : "0.0.0.0"), "older preferences keep role-specific connection defaults"); form.Close(); }
            string invalid = Path.Combine(folder, "invalid.json"); File.WriteAllText(invalid, "{\"Version\":1,\"Communication\":{\"Mode\":999,\"Baud\":123,\"Parity\":\"bad\",\"StopBits\":\"3\",\"DataBits\":9,\"Port\":0,\"Unit\":999,\"TimeoutMs\":0,\"PollIntervalMs\":0}}");
            using (Form form = Create(type, invalid))
            { Check(((ComboBox)Field(form, "baud")).Text == "9600" && ((ComboBox)Field(form, "parity")).Text == "None" && ((ComboBox)Field(form, "stopBits")).Text == "1" && ((NumericUpDown)Field(form, "port")).Value == 502 && ((NumericUpDown)Field(form, "unit")).Value == 1, "invalid remembered options fall back without blocking startup"); form.Close(); }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
