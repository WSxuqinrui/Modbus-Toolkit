using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class LayoutMemory281Checks
{
    private static int checks;
    private static object Field(object source, string name)
    { return source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(source); }
    private static object Call(object source, string name, params object[] args)
    { return source.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(source, args); }
    private static object Property(object source, string name)
    { return source.GetType().GetProperty(name).GetValue(source, null); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Pump() { Application.DoEvents(); Application.DoEvents(); }
    private static bool HasButton(Control root, string text)
    { if (root is Button && root.Text == text) return true; foreach (Control child in root.Controls) if (HasButton(child, text)) return true; return false; }
    private static Form Create(Type type, string path)
    { return (Form)Activator.CreateInstance(type, new object[] { path }); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0])); Type type = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            Check(type.GetConstructor(new Type[] { typeof(string) }) != null, "layout store supports an isolated persistence path");
            string directory = Path.Combine(Path.GetFullPath(args[1]), "LayoutMemory-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            for (int mask = 0; mask < 8; mask++)
            {
                string path = Path.Combine(directory, "state-" + mask + ".json");
                using (Form form = Create(type, path))
                {
                    form.Show(); Pump(); ((SplitContainer)Field(form, "BodySplit")).SplitterDistance = 335;
                    object traffic = Field(form, "trafficWorkspace"); Call(traffic, "RestoreLayout", false, false, 132);
                    if ((mask & 1) != 0) ((Button)Field(form, "SidebarToggle")).PerformClick();
                    if ((mask & 2) != 0) ((Button)Field(form, "LogToggle")).PerformClick();
                    if ((mask & 4) != 0) ((Button)Field(form, "RawSendToggle")).PerformClick();
                    form.Close();
                }
                Check(File.Exists(path), "accepted close saves layout " + mask);
                using (Form reopened = Create(type, path))
                {
                    reopened.Show(); Pump(); object traffic = Field(reopened, "trafficWorkspace");
                    Check(((SplitContainer)Field(reopened, "BodySplit")).Panel1Collapsed == ((mask & 1) != 0) &&
                        (bool)Property(traffic, "LogCollapsed") == ((mask & 2) != 0) && (bool)Property(traffic, "RawCollapsed") == ((mask & 4) != 0), "reopen restores all three independent states " + mask);
                    Check(Math.Abs(((SplitContainer)Field(reopened, "BodySplit")).SplitterDistance - 335) <= 2 && Math.Abs((int)Property(traffic, "ExpandedLogHeight") - 132) <= 3, "reopen restores expanded divider sizes " + mask);
                    reopened.Close();
                }
            }
            string corrupt = Path.Combine(directory, "corrupt.json"); File.WriteAllText(corrupt, "{invalid");
            using (Form form = Create(type, corrupt)) { form.Show(); Pump(); Check(!((SplitContainer)Field(form, "BodySplit")).Panel1Collapsed && !(bool)Property(Field(form, "trafficWorkspace"), "LogCollapsed"), "corrupt preferences fall back to default layout"); form.Close(); }
            string blocker = Path.Combine(directory, "blocker"); File.WriteAllText(blocker, "a file is not a folder");
            using (Form form = Create(type, Path.Combine(blocker, "layout.json"))) { form.Show(); Pump(); form.Close(); Check(form.IsDisposed, "settings write failure does not block close"); }
            using (Form form = Create(type, null))
            {
                form.Show(); Pump(); ((Timer)Field(form, "timer")).Stop();
                Check(!HasButton(form, "复制选中") && !HasButton(form, "复制全部"), "redundant copy buttons removed from toolbar");
                RichTextBox log = (RichTextBox)Field(form, "logs"); Check(log.ContextMenuStrip.Items.Count >= 3, "right-click copying remains available");
                Control raw = (Control)Field(form, "rawSender"); raw.Enabled = true;
                string[] targets = new string[0]; raw.GetType().GetProperty("GetTargets").SetValue(raw, new Func<string[]>(delegate { return targets; }), null);
                Call(raw, "RefreshTargets"); Control group = (Control)Field(raw, "targetGroup");
                Check(!group.Visible, "disconnected raw panel omits target controls");
                targets = new string[] { "A" }; Call(raw, "RefreshTargets"); ComboBox target = (ComboBox)Field(raw, "TargetCombo");
                Check(!group.Visible && target.SelectedItem as string == "A", "one endpoint is selected without showing a target dropdown");
                targets = new string[] { "A", "B" }; Call(raw, "RefreshTargets"); Check(group.Visible, "multiple endpoints still expose explicit selection");
                targets = new string[] { "B" }; Call(raw, "RefreshTargets"); Check(group.Visible && target.SelectedIndex == -1, "lost selected peer requires explicit reselection even with one remaining peer");
                ((TextBox)Field(raw, "InputBox")).Text = "01 03"; int sent = 0;
                raw.GetType().GetProperty("SendRequested").SetValue(raw, new Func<string, byte[], bool>(delegate(string endpoint, byte[] bytes) { sent++; return true; }), null);
                ((Button)Field(raw, "SendButton")).PerformClick(); Check(sent == 0, "lost-target submission never silently reroutes");
                target.SelectedIndex = 0; ((Button)Field(raw, "SendButton")).PerformClick(); Check(sent == 1 && !group.Visible, "explicit reselection restores compact single-endpoint sending");
                Label status = (Label)Field(form, "notice"); object[] areas = (object[])Field(form, "Areas");
                if (type.Namespace == "ModbusMaster")
                {
                    Call(areas[2], "SetQuality", new string('长', 240), false); Call(form, "TickUi");
                    Check(status.Text.Length < 75 && ((string)Property(status, "Details")).Contains(new string('长', 240)), "status footer is concise while tooltip retains full area details");
                }
                status.Text = "保存路径：" + new string('x', 500);
                Check(status.AutoEllipsis && (string)Property(status, "Details") == status.Text, "long status notices remain available in full hover text");
                form.Size = form.MinimumSize; Pump();
                Check(((TextBox)Field(raw, "InputBox")).Height >= 40, "compact sender remains usable in minimum window");
                form.Close();
            }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
