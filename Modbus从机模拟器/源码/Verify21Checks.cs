using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ModbusSimulator;
internal static class Verify21Checks
{
    private static int checks;
    private static void Check(bool yes, string name) { if (!yes) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static T Field<T>(object source, string name) { return (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source); }
    private static void Pump(int milliseconds)
    { DateTime stop = DateTime.UtcNow.AddMilliseconds(milliseconds); do { Application.DoEvents(); Thread.Sleep(5); } while (DateTime.UtcNow < stop); }
    private static void Capture(Form form, string file)
    { using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(file, System.Drawing.Imaging.ImageFormat.Png); } }
    private static AddressCard CardAt(CardView view, int area, int address)
    {
        view.Locate(area, address);
        foreach (AddressCard card in view.Cards) if (card.AreaId == area && card.Address == address) return card;
        throw new Exception("Card not found");
    }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm())
            {
                form.Show(); Pump(80);
                ComboBox transport = Field<ComboBox>(form, "mode"), bits = Field<ComboBox>(form, "dataBits");
                Check(transport.Items.Count == 7, "ASCII and additional network modes appear alongside existing transports");
                form.SelectTransport(3); bits.SelectedItem = "7";
                Check(bits.Enabled && form.CaptureConfiguration().DataBits == 7 && form.CaptureConfiguration().Mode == 3, "ASCII data bits settings captured");
                form.SelectTransport(0); Check(!bits.Enabled && bits.Text == "8", "RTU/TCP raw modes retain 8-bit setting");
                int width = form.Views.Width; form.SidebarToggle.PerformClick(); Pump(30);
                Check(form.BodySplit.Panel1Collapsed && form.Views.Width > width, "sidebar collapses and display grows");
                form.SidebarToggle.PerformClick(); Pump(30); Check(!form.BodySplit.Panel1Collapsed, "sidebar expands again");
                TrafficLog log = Field<TrafficLog>(form, "logs");
                log.Clear(); log.AppendLines(new string[] { "12:00:00.000 RX: 01 03", "12:00:00.001 TX: 01 03 02" });
                log.Select(0, "12:00:00.000 RX: 01 03".Length);
                Check(log.CopyText(false).Contains("RX:") && !log.CopyText(false).Contains("TX:"), "log selected text copy source");
                Check(log.CopyText(true).Contains("RX:") && log.CopyText(true).Contains("TX:"), "log all-text copy source");
                Check(TrafficLog.LineColor("T RX: 01") != TrafficLog.LineColor("T TX: 01"), "RX and TX have different colors");
                ComboBox mode = Field<ComboBox>(form, "displayMode"); mode.SelectedIndex = 4; Pump(80);
                Check(form.Views.CurrentMode == 4 && form.Views.Cards.TotalCardCount == 40 && form.Views.Cards.Cards.Count < 40, "card view uses continuous scroll with viewport controls");
                CardView view = form.Views.Cards; AddressCard first = view.Cards[0]; first.Header.PerformClick();
                Check(first.IsExpanded && first.Height == 104 && first.Editor.Parent != first, "card opens shared editor without changing size");
                ParseRule floatRule = new ParseRule { DataType = "Float32", ByteOrder = "ABCD", Operation = "*", Operand = 2, Offset = 1, Unit = "kPa" };
                form.Areas[2].SetParseRule(0, floatRule); form.Bank.WriteWords(2, 0, new ushort[] { 0x4148, 0 });
                first = CardAt(view, 2, 0); first.RefreshValue(); Check(first.ValueLabel.Text.Contains("26.000") && first.ValueLabel.Text.Contains("kPa"), "card decodes IEEE float and engineering conversion");
                first.WriteValue("51"); Check(form.Bank.Get(2, 0) == 0x41C8 && form.Bank.Get(2, 1) == 0, "card engineering edit inversely encodes all raw words");
                Check(form.Areas[2].ParsedValue(0).Contains("51.000"), "table parsed column shares card rule");
                form.Areas[2].SetParseRule(4, new ParseRule { DataType = "Float64", ByteOrder = "DCBA" });
                AreaPresentation.Write(form.Bank, 2, 4, -12.5, form.Areas[2].Presentation.GetRule(4)); CardAt(view, 2, 4).RefreshValue();
                Check(CardAt(view, 2, 4).ValueLabel.Text.Contains("-12.500"), "Float64 little-endian card");
                form.Areas[2].SetParseRule(8, new ParseRule { DataType = "Int16" }); form.Bank.Set(2, 8, 65535); CardAt(view, 2, 8).RefreshValue();
                Check(CardAt(view, 2, 8).ValueLabel.Text.Contains("-1.000"), "signed card preserves raw uint16 bits");
                form.Areas[2].SetParseRule(3, new ParseRule { DataType = "UInt16", Operand = 0.1 });
                form.Bank.Set(2, 3, 3); CardAt(view, 2, 3).WriteValue("0.3");
                Check(form.Bank.Get(2, 3) == 3, "decimal engineering scaling writes without false fractional error");
                AddressCard bit = CardAt(view, 0, 0); Pump(30); bit.Switch.Checked = true;
                Check(form.Bank.Get(0, 0) == 1 && bit.Switch.Text.Contains("ON"), "bit switch changes shared coil state");
                form.Bank.Set(0, 0, 0); bit.RefreshValue(); Check(!bit.Switch.Checked && bit.Switch.Text.Contains("OFF"), "external bit change refreshes switch indicator");
                CardAt(view, 1, 0).Switch.Checked = true; Pump(30);
                Check(form.Bank.Get(1, 0) == 1 && form.Bank.Get(0, 0) == 0, "input switch simulation independent from coils");
                first = CardAt(view, 2, 0); first.ValueInput.Text = "99";
                mode.SelectedIndex = 0; mode.SelectedIndex = 4; Pump(30);
                Check(view.Cards[0] == first && first.ValueInput.Text == "99", "switching view preserves unsubmitted card draft");
                string path = Path.Combine(args[0], "2.2-UI配置.json"); form.CaptureConfiguration().Save(path);
                form.Areas[2].Presentation.Rules.Clear(); form.ApplyConfiguration(SimulatorConfiguration.Load(path));
                Check(form.Areas[2].Presentation.GetRule(0).DataType == "Float32" && form.Areas[2].Presentation.GetRule(4).ByteOrder == "DCBA", "per-address rules restore with raw values");
                form.Areas[2].ApplyDefinition(0, 1); form.Areas[2].SetParseRule(0, floatRule); view.SelectArea(2); view.Rebuild(); view.Cards[0].RefreshValue();
                Check(view.Cards[0].ValueLabel.Text == "无法解析", "short final register card reports missing words");
                ushort original = form.Bank.Get(2, 0);
                try { view.Cards[0].WriteValue("51"); throw new Exception("short write accepted"); } catch (ArgumentException) { }
                Check(form.Bank.Get(2, 0) == original, "short parsed write cannot partially corrupt raw data");
                form.Areas[2].ApplyDefinition(0, 16); form.Areas[2].Presentation.Rules.Clear();
                form.Areas[2].SetParseRule(0, floatRule); form.Bank.WriteWords(2, 0, new ushort[] { 0x4148, 0 });
                view.SelectArea(2); view.Rebuild(); view.Cards[0].ToggleExpanded(); Pump(80);
                Capture(form, Path.Combine(args[0], "2.2卡片寄存器.png"));
                view.SelectArea(0); Pump(50); Capture(form, Path.Combine(args[0], "2.2卡片开关量.png"));
                form.SidebarToggle.PerformClick(); Pump(50); Capture(form, Path.Combine(args[0], "2.2收起设置.png"));
                form.SidebarToggle.PerformClick(); mode.SelectedIndex = 0;
                using (RuleEditor dialog = new RuleEditor(floatRule, "解析设置预览")) { dialog.Show(form); Pump(30); Capture(dialog, Path.Combine(args[0], "2.2解析设置.png")); dialog.Close(); }
                Console.WriteLine("TOTAL " + checks + ", FAILED 0"); form.Close();
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
