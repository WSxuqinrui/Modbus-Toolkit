using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ModbusSimulator;
internal static class Verify22Checks
{
    private static int passed, failed;
    private static T Field<T>(object source, string name) { return (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source); }
    private static void Test(string label, Func<bool> action)
    { try { if (!action()) throw new Exception("condition failed"); passed++; Console.WriteLine("PASS " + label); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + label + ": " + ex.Message); } }
    private static int AreaOf(AddressCard card) { return Field<AreaPanel>(card, "area").Area; }
    private static int CountCombo(Control parent)
    { int count = parent is ComboBox ? 1 : 0; foreach (Control child in parent.Controls) count += CountCombo(child); return count; }
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (MainForm form = new MainForm())
        {
            form.Show(); Application.DoEvents();
            SimulatorConfiguration initial = form.CaptureConfiguration();
            ComboBox mode = Field<ComboBox>(form, "mode"), baud = Field<ComboBox>(form, "baud");
            Test("each newly opened data area defaults to 10", delegate { for (int i = 0; i < 4; i++) if (form.Bank.Count(i) != 10) return false; return true; });
            int[] expectedIds = { 1, 3, 2, 0, 4, 5, 6 };
            for (int i = 0; i < expectedIds.Length; i++)
            {
                int index = i, id = expectedIds[i];
                Test("dropdown index " + i + " persists correct transport id " + id, delegate { mode.SelectedIndex = index; return form.CaptureConfiguration().Mode == id; });
            }
            Test("baud values are ascending", delegate { int previous = 0; foreach (object item in baud.Items) { int n = int.Parse(item.ToString()); if (n <= previous) return false; previous = n; } return true; });
            foreach (AreaPanel panel in form.Areas)
            {
                AreaPanel selected = panel;
                Test("range start and count use separate rows area " + panel.Area, delegate { return selected.StartInput.Parent == selected.CountInput.Parent && selected.StartInput.Top < selected.CountInput.Top; });
            }
            ComboBox view = Field<ComboBox>(form, "displayMode"); view.SelectedIndex = 4; Application.DoEvents();
            Test("card page groups holding before input", delegate { return form.Views.Cards.Cards[0].AreaId == 2 && form.Views.Cards.Cards[9].AreaId == 2 && form.Views.Cards.Cards[10].AreaId == 3; });
            Test("card view has no area dropdown", delegate { return CountCombo(form.Views.Cards) == 0; });
            form.Views.Cards.Locate(0, 0);
            AddressCard bit = null; foreach (AddressCard card in form.Views.Cards.Cards) if (AreaOf(card) == 0) { bit = card; break; }
            Test("status lamp is compact 32 pixels", delegate { Control lamp = Field<Control>(bit, "lamp"); return lamp.Width == 32 && lamp.Height == 32; });
            Test("switch updates model and restores from model", delegate { if (bit == null) return false; bit.Switch.Checked = true; if (form.Bank.Get(0, bit.Address) != 1) return false; form.Bank.Set(0, bit.Address, 0); bit.RefreshValue(); return !bit.Switch.Checked; });
            Test("legacy config transport restored by stable id and saved quantities preserved", delegate { SimulatorConfiguration config = SimulatorConfiguration.Load(Path.Combine(Path.GetDirectoryName(args[0]), "四区联调示例.json")); form.ApplyConfiguration(config); return form.CaptureConfiguration().Mode == 2 && form.Bank.Count(2) == 64; });
            for (int area = 0; area < 4; area++) form.Areas[area].ApplyDefinition(100 * (area + 1), area + 1);
            Test("mixed cards cover unequal area sizes exactly once", delegate
            {
                form.Views.Cards.Rebuild(); System.Collections.Generic.HashSet<string> found = new System.Collections.Generic.HashSet<string>();
                foreach (AddressCard card in form.Views.Cards.Cards)
                    if (card.Address < form.Bank.Start(card.AreaId) || card.Address >= form.Bank.Start(card.AreaId) + form.Bank.Count(card.AreaId) || !found.Add(card.AreaId + ":" + card.Address)) return false;
                return found.Count == 10;
            });
            for (int area = 0; area < 4; area++) form.Bank.Configure(area, 0, 65536);
            Test("mixed card locate reaches last address in full four-area space", delegate
            {
                form.Views.Cards.Locate(1, 65535);
                foreach (AddressCard card in form.Views.Cards.Cards) if (card.AreaId == 1 && card.Address == 65535) return true;
                return false;
            });
            Test("2.2 supplied demo keeps ten values per area and valid Float64 tail", delegate
            {
                SimulatorConfiguration demo = SimulatorConfiguration.Load(Path.Combine(Path.GetDirectoryName(args[0]), "2.2卡片解析示例.json"));
                foreach (AreaConfiguration section in demo.Areas) if (section.Count != 10) return false;
                RegisterBank model = demo.CreateBank(); AreaPresentation presentation = new AreaPresentation(); presentation.Import(demo.Areas[2]);
                return AreaPresentation.Read(model, 2, 6, presentation.GetRule(6)) == 12.5;
            });
            form.ApplyConfiguration(initial); form.Bank.WriteWords(2, 0, new ushort[] { 0x4148, 0 });
            form.Areas[2].SetParseRule(0, new ParseRule { DataType = "Float32", Operand = 2, Offset = 1, Unit = "kPa" });
            form.Bank.Set(3, 0, 1234); form.Bank.Set(0, 1, 1); form.Bank.Set(1, 1, 1);
            view.SelectedIndex = 4; form.Views.RefreshValues(); Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(args[0], "2.2界面预览.png"), System.Drawing.Imaging.ImageFormat.Png); }
            form.SidebarToggle.PerformClick(); Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(args[0], "2.2四区卡片.png"), System.Drawing.Imaging.ImageFormat.Png); }
            form.Close();
        }
        Console.WriteLine("TOTAL " + (passed + failed) + ", PASSED " + passed + ", FAILED " + failed); return failed == 0 ? 0 : 1;
    }
}
