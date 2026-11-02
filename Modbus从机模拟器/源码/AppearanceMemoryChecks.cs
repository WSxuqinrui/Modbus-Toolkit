using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class AppearanceMemoryChecks
{
    private static int checks;
    private static object Field(object source, string name)
    { return source.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(source); }
    private static object Call(object source, string name, params object[] values)
    { return source.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(source, values); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Pump() { Application.DoEvents(); Application.DoEvents(); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1");
            Application.EnableVisualStyles(); Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type type = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            Type language = assembly.GetType(type.Namespace + ".UiLanguage"), theme = assembly.GetType(type.Namespace + ".UiTheme");
            Check(language != null && theme != null, "language and theme modules present in actual release");
            string directory = Path.Combine(Path.GetFullPath(args[1]), "AppearanceMemory-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            for (int locale = 0; locale < 5; locale++) for (int dark = 0; dark < 2; dark++)
            {
                string path = Path.Combine(directory, locale + "-" + dark + ".json");
                using (Form form = (Form)Activator.CreateInstance(type, new object[] { path }))
                {
                    form.Show(); Pump(); ((Timer)Field(form, "timer")).Stop();
                    object[] areas = (object[])Field(form, "Areas"); object presentation = Field(areas[2], "Presentation");
                    Call(presentation, "SetLabel", 0, "连接设置", "kPa");
                    Control raw = (Control)Field(form, "rawSender"); TextBox input = (TextBox)Field(raw, "InputBox"); input.Text = "01 03 FA";
                    object bank = Field(form, "bank"); object revision = bank.GetType().GetProperty("Revision").GetValue(bank, null);
                    ((ComboBox)Field(form, "displayMode")).SelectedIndex = 4;
                    ((ComboBox)Field(form, "languageMode")).SelectedIndex = locale;
                    ((ComboBox)Field(form, "themeMode")).SelectedIndex = dark; Pump();
                    if (type.Namespace == "ModbusSimulator") Check(((string)Field(form, "notice").GetType().GetProperty("Details").GetValue(Field(form, "notice"), null)).Contains("0x"), "language changes retain the complete footer details " + locale + "/" + dark);
                    Check((int)language.GetProperty("Current").GetValue(null, null) == locale && (int)theme.GetProperty("Current").GetValue(null, null) == dark, "selectors immediately apply language/theme " + locale + "/" + dark);
                    Check(input.Text == "01 03 FA" && revision.Equals(bank.GetType().GetProperty("Revision").GetValue(bank, null)), "appearance never alters payload/registers " + locale + "/" + dark);
                    object cards = Field(Field(form, "Views"), "Cards"); Call(cards, "Locate", 2, 0); Pump();
                    bool customFound = false;
                    foreach (object card in (System.Collections.IEnumerable)cards.GetType().GetProperty("Cards").GetValue(cards, null))
                        if ((int)card.GetType().GetProperty("AreaId").GetValue(card, null) == 2 && (int)Field(card, "Address") == 0) customFound = ((Button)Field(card, "Header")).Text.Contains("连接设置");
                    Check(customFound && (string)Call(presentation, "GetUnit", 0) == "kPa", "custom metadata survives UI translation " + locale + "/" + dark);
                    RichTextBox log = (RichTextBox)Field(form, "logs");
                    Check(dark == 1 ? log.BackColor.GetBrightness() < .4 : log.BackColor.GetBrightness() > .8, "traffic background follows selected theme " + locale + "/" + dark);
                    form.Close();
                }
                using (Form reopened = (Form)Activator.CreateInstance(type, new object[] { path }))
                {
                    reopened.Show(); Pump();
                    Check(((ComboBox)Field(reopened, "languageMode")).SelectedIndex == locale && ((ComboBox)Field(reopened, "themeMode")).SelectedIndex == dark, "close/reopen restores language/theme " + locale + "/" + dark);
                    Check(!(bool)Field(reopened, "rawSender").GetType().GetProperty("IsCycling").GetValue(Field(reopened, "rawSender"), null), "reopen keeps automatic transmission stopped " + locale + "/" + dark);
                    reopened.Close();
                }
            }
            Console.WriteLine("TOTAL " + checks + ", FAILED 0"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
