using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class CycleSendChecks
{
    private static int checks;
    private static object Field(object source, string name)
    { FieldInfo value = source.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); return value == null ? null : value.GetValue(source); }
    private static object Call(object source, string name, params object[] args)
    { return source.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(source, args); }
    private static bool Cycling(Control panel) { return (bool)panel.GetType().GetProperty("IsCycling").GetValue(panel, null); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Pump(int milliseconds)
    { Stopwatch watch = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Assembly assembly = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0]));
            Type type = assembly.GetType("ModbusSimulator.RawSendPanel") ?? assembly.GetType("ModbusMaster.RawSendPanel");
            using (Form host = new Form { ClientSize = new Size(900, 240) })
            using (Control panel = (Control)Activator.CreateInstance(type))
            {
                Check(Field(panel, "CycleCheck") is CheckBox && Field(panel, "PeriodInput") is NumericUpDown, "cycle switch and editable interval are available");
                panel.Dock = DockStyle.Fill; host.Controls.Add(panel); host.Show(); Application.DoEvents();
                CheckBox cycle = (CheckBox)Field(panel, "CycleCheck"); NumericUpDown period = (NumericUpDown)Field(panel, "PeriodInput");
                TextBox input = (TextBox)Field(panel, "InputBox"); string[] targets = new string[] { "A" }; bool busy = false; int sent = 0; string last = null;
                type.GetProperty("GetTargets").SetValue(panel, new Func<string[]>(delegate { return targets; }), null);
                type.GetProperty("IsSendBusy").SetValue(panel, new Func<bool>(delegate { return busy; }), null);
                type.GetProperty("SendRequested").SetValue(panel, new Func<string, byte[], bool>(delegate(string endpoint, byte[] bytes) { last = endpoint + ":" + BitConverter.ToString(bytes); sent++; busy = true; return true; }), null);
                Call(panel, "RefreshTargets"); input.Text = "01 03"; period.Value = 50;
                Check(!Cycling(panel) && period.Minimum == 20 && period.Maximum == 600000, "cycle is off by default with bounded millisecond interval");
                cycle.Checked = true;
                Check(sent == 1 && Cycling(panel) && input.ReadOnly && !period.Enabled && !((Button)Field(panel, "SendButton")).Enabled, "start sends once and freezes active packet settings");
                Pump(170); Check(sent == 1, "busy periods do not enqueue more sends");
                busy = false; Pump(90); Check(sent == 2 && last == "A:01-03", "next idle period sends original bytes to original target");
                targets = new string[] { "B" }; Pump(90);
                Check(!Cycling(panel) && !cycle.Checked && sent == 2, "target disappearance stops cycle without rerouting");
                ((ComboBox)Field(panel, "TargetCombo")).SelectedIndex = 0; busy = false; cycle.Checked = true;
                Check(sent == 3 && Cycling(panel), "explicit target reselection permits a new cycle");
                panel.Enabled = false; Pump(90); Check(!Cycling(panel) && !cycle.Checked && sent == 3, "disabling disconnected sender stops cycle immediately");
                panel.Enabled = true; Pump(90); Check(!Cycling(panel) && sent == 3 && !input.ReadOnly, "re-enable never automatically resumes transmission");
                busy = false; type.GetProperty("SendRequested").SetValue(panel, new Func<string, byte[], bool>(delegate { sent++; return false; }), null);
                cycle.Checked = true; Pump(90); Check(!Cycling(panel) && sent == 4, "rejected send stops instead of retrying on every period");
                input.Text = "Z"; cycle.Checked = true; Check(!Cycling(panel) && sent == 4, "invalid payload never starts a cycle");
                input.Text = "01"; type.GetProperty("IsSendBusy").SetValue(panel, new Func<bool>(delegate { throw new InvalidOperationException("test busy check"); }), null);
                cycle.Checked = true; Check(!Cycling(panel), "scheduler callback failures stop cleanly");
                type.GetProperty("IsSendBusy").SetValue(panel, new Func<bool>(delegate { return false; }), null);
                type.GetProperty("SendRequested").SetValue(panel, new Func<string, byte[], bool>(delegate { sent++; return true; }), null);
                cycle.Checked = true; int beforeDispose = sent; panel.Dispose(); Pump(100); Check(sent == beforeDispose, "disposing sender removes timer and prevents later sends");
                host.Close();
            }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
