using System;
using System.IO;
using System.Web.Script.Serialization;
using ModbusSimulator;

internal static class MetadataChecks
{
    private static int passed;
    private static void Check(bool success, string name)
    { if (!success) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { Check(true, name); return; }
        throw new Exception("not rejected: " + name);
    }
    private static SimulatorConfiguration Config()
    {
        RegisterBank bank = new RegisterBank();
        bank.Configure(0, 5, 1); bank.Configure(1, 10, 1); bank.Configure(2, 100, 5); bank.Configure(3, 200, 2);
        return SimulatorConfiguration.Capture(bank);
    }
    private static AddressLabel Label(int address, string name, string unit)
    { return new AddressLabel { Address = address, Name = name, Unit = unit }; }
    public static int Main(string[] args)
    {
        try
        {
            Check(new SimulatorConfiguration().Version == 4, "new metadata configuration uses version 4");
            SimulatorConfiguration emptyConfig = Config();
            for (int areaIndex = 0; areaIndex < 4; areaIndex++)
            { emptyConfig.Areas[areaIndex].Count = 0; emptyConfig.Areas[areaIndex].Values = new ushort[0]; }
            emptyConfig.Validate();
            RegisterBank emptyBank = emptyConfig.CreateBank();
            Check(emptyBank.Count(2) == 0 && emptyBank.Start(2) == 100 && emptyBank.Snapshot()[2].Values.Length == 0, "empty areas restore with legal starts and empty snapshots");
            string emptyPath = Path.Combine(args.Length == 0 ? Path.GetTempPath() : args[0], "empty-metadata-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                emptyConfig.Save(emptyPath); SimulatorConfiguration emptyLoaded = SimulatorConfiguration.Load(emptyPath);
                Check(emptyLoaded.Areas[0].Count == 0 && emptyLoaded.Areas[2].Values.Length == 0 && emptyLoaded.CreateBank().Count(3) == 0, "all-empty configuration file roundtrip");
            }
            finally { if (File.Exists(emptyPath)) File.Delete(emptyPath); }
            emptyConfig.Areas[2].ItemLabels = new AddressLabel[] { Label(100, "absent", "") };
            Reject(delegate { emptyConfig.Validate(); }, "empty area rejects labels");
            emptyConfig.Areas[2].ItemLabels = new AddressLabel[0];
            emptyConfig.Areas[2].ItemRules = new AddressRule[] { new AddressRule { Address = 100, Rule = new ParseRule() } };
            Reject(delegate { emptyConfig.Validate(); }, "empty area rejects address rules");
            emptyConfig.Areas[2].ItemRules = new AddressRule[0];
            emptyConfig.CardOrder = new CardKey[] { new CardKey { Area = 2, Address = 100 } };
            Reject(delegate { emptyConfig.Validate(); }, "empty area rejects custom order keys");
            emptyConfig.CardOrder = new CardKey[0]; emptyConfig.Areas[2].Values = new ushort[] { 1 };
            Reject(delegate { emptyConfig.Validate(); }, "empty area rejects nonempty values array");
            emptyConfig.Areas[2].Values = new ushort[0]; emptyConfig.Areas[2].Start = 65536;
            Reject(delegate { emptyConfig.Validate(); }, "empty configuration rejects illegal start");
            AreaPresentation view = new AreaPresentation(); view.DefaultRule.Unit = "mA";
            Check(view.GetName(100) == "" && view.GetUnit(100) == "mA", "missing label falls back to rule unit and empty name");
            view.Rules.Add(101, new ParseRule { Unit = "V" });
            Check(view.GetUnit(101) == "V", "unit fallback uses address-specific parsing rule");
            view.SetLabel(100, "电流", "A"); view.SetLabel(101, "电压", "");
            Check(view.GetName(100) == "电流" && view.GetUnit(100) == "A" && view.GetUnit(101) == "", "explicit label unit overrides parsing including empty unit");
            AddressLabel[] exported = view.ExportLabels();
            Check(exported.Length == 2 && exported[0].Address == 100 && exported[1].Address == 101, "label export sorted by absolute address");
            exported[0].Name = "changed"; exported[1].Unit = "changed";
            Check(view.GetName(100) == "电流" && view.GetUnit(101) == "", "label export is a deep copy");
            AreaConfiguration area = new AreaConfiguration { DefaultRule = new ParseRule { Unit = "Pa" }, ItemLabels = view.ExportLabels() };
            AreaPresentation imported = new AreaPresentation(); imported.Import(area);
            area.ItemLabels[0].Name = "changed";
            Check(imported.GetName(100) == "电流" && imported.GetUnit(100) == "A", "label import is a deep copy");
            imported.Trim(101, 1);
            Check(imported.Labels.Count == 1 && imported.GetName(100) == "" && imported.GetName(101) == "电压", "trim removes labels outside new range");
            imported.Import(new AreaConfiguration());
            Check(imported.Labels.Count == 0 && imported.GetName(101) == "", "import clears stale labels");
            view.SetLabel(102, new string('n', 64), new string('u', 32));
            Check(view.GetName(102).Length == 64 && view.GetUnit(102).Length == 32, "maximum allowed metadata lengths accepted");
            Reject(delegate { view.SetLabel(100, new string('n', 65), "A"); }, "long label name rejected");
            Reject(delegate { view.SetLabel(100, "电流", new string('u', 33)); }, "long label unit rejected");
            Reject(delegate { view.SetLabel(100, null, "A"); }, "null label name rejected");
            Reject(delegate { view.SetLabel(100, "电流", null); }, "null label unit rejected");
            Check(view.GetName(100) == "电流" && view.GetUnit(100) == "A", "failed label updates preserve prior values");

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            foreach (int version in new int[] { 2, 3 })
            {
                // 字面量旧配置没有 ItemLabels/CardOrder 字段，确保兼容真实旧文件。
                string oldJson = "{\"Version\":" + version + ",\"Areas\":[{\"Start\":5,\"Count\":1,\"Values\":[1]},{\"Start\":10,\"Count\":1,\"Values\":[0]},{\"Start\":100,\"Count\":1,\"Values\":[123]},{\"Start\":200,\"Count\":1,\"Values\":[456]}]}";
                SimulatorConfiguration legacy = serializer.Deserialize<SimulatorConfiguration>(oldJson); legacy.Validate();
                Check(legacy.CardOrder.Length == 0 && legacy.Areas[2].ItemLabels.Length == 0 && legacy.CreateBank().Get(2, 100) == 123,
                    "legacy v" + version + " without metadata restores original data");
            }
            SimulatorConfiguration valid = Config();
            valid.Areas[2].ItemLabels = new AddressLabel[] { Label(101, "温度", "℃") };
            valid.CardOrder = new CardKey[] { new CardKey { Area = 3, Address = 200 }, new CardKey { Area = 2, Address = 101 } };
            valid.Validate();
            string directory = args.Length == 0 ? Path.GetTempPath() : args[0];
            string path = Path.Combine(directory, "metadata-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                valid.Save(path); SimulatorConfiguration loaded = SimulatorConfiguration.Load(path);
                Check(loaded.Version == 4 && loaded.Areas[2].ItemLabels[0].Name == "温度" && loaded.Areas[2].ItemLabels[0].Unit == "℃" &&
                    loaded.CardOrder.Length == 2 && loaded.CardOrder[0].Area == 3 && loaded.CardOrder[1].Address == 101,
                    "v4 labels and card ordering persist through file roundtrip");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            SimulatorConfiguration config = Config(); config.Areas[2].ItemLabels = null; config.CardOrder = null; config.Validate();
            Check(config.Areas[2].ItemLabels.Length == 0 && config.CardOrder.Length == 0, "null optional metadata arrays normalize to empty");
            foreach (AddressLabel invalid in new AddressLabel[] { null, Label(99, "", ""), Label(105, "", ""), Label(100, null, ""), Label(100, "", null), Label(100, new string('n', 65), ""), Label(100, "", new string('u', 33)) })
            {
                SimulatorConfiguration bad = Config(); bad.Areas[2].ItemLabels = new AddressLabel[] { invalid };
                Reject(delegate { bad.Validate(); }, "invalid configured label rejected");
            }
            config = Config(); config.Areas[2].ItemLabels = new AddressLabel[] { Label(100, "a", ""), Label(100, "b", "") };
            Reject(delegate { config.Validate(); }, "duplicate label address rejected");
            foreach (CardKey invalid in new CardKey[] { null, new CardKey { Area = -1, Address = 100 }, new CardKey { Area = 4, Address = 100 }, new CardKey { Area = 2, Address = 99 }, new CardKey { Area = 2, Address = 105 } })
            {
                SimulatorConfiguration bad = Config(); bad.CardOrder = new CardKey[] { invalid };
                Reject(delegate { bad.Validate(); }, "invalid custom card key rejected");
            }
            config = Config(); config.CardOrder = new CardKey[] { new CardKey { Area = 2, Address = 100 }, new CardKey { Area = 2, Address = 100 } };
            Reject(delegate { config.Validate(); }, "duplicate custom card keys rejected");
            config = Config(); config.Version = 5;
            Reject(delegate { config.Validate(); }, "unknown configuration version rejected");
            Console.WriteLine("TOTAL " + passed + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
