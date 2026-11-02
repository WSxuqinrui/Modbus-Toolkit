using System;
using System.IO;
using ModbusSimulator;
internal static class PresentationChecks
{
    private static int count;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    public static int Main(string[] args)
    {
        try
        {
            RegisterBank bank = new RegisterBank(); bank.Configure(2, 100, 3);
            ParseRule rule = new ParseRule { DataType = "Float32", ByteOrder = "ABCD", Operation = "*", Operand = 2, Offset = 1 };
            AreaPresentation.Write(bank, 2, 100, 26, rule);
            Check(bank.Get(2, 100) == 0x4148 && bank.Get(2, 101) == 0, "parsed Float32 writes exact raw words");
            Check(AreaPresentation.Read(bank, 2, 100, rule) == 26, "parsed value readback");
            long before = bank.Revision;
            try { bank.WriteWords(2, 102, new ushort[] { 1, 2 }); throw new Exception("tail write accepted"); } catch (ArgumentException) { }
            Check(bank.Get(2, 102) == 0 && before == bank.Revision, "invalid multiword write makes no partial change");
            try { bank.WriteWords(0, 0, new ushort[] { 1, 2 }); throw new Exception("bit write accepted"); } catch (ArgumentException) { }
            Check(bank.Get(0, 0) == 0, "invalid bit bulk write is atomic");
            SimulatorConfiguration config = SimulatorConfiguration.Capture(bank); config.Mode = 3; config.DataBits = 7;
            config.Areas[2].DefaultRule = AreaPresentation.Copy(rule);
            config.Areas[2].ItemRules = new AddressRule[] { new AddressRule { Address = 100, Rule = new ParseRule { DataType = "Int32", ByteOrder = "CDAB", Operation = "/", Operand = 10 } } };
            string file = Path.Combine(args[0], "2.1解析配置回读.json"); config.Save(file);
            SimulatorConfiguration loaded = SimulatorConfiguration.Load(file);
            Check(loaded.DataBits == 7 && loaded.Mode == 3 && loaded.Areas[2].ItemRules[0].Rule.ByteOrder == "CDAB" && loaded.Areas[2].DefaultRule.Offset == 1, "ASCII parameters and per-item parse rule roundtrip");
            AreaPresentation view = new AreaPresentation(); view.Import(loaded.Areas[2]);
            loaded.Areas[2].DefaultRule.Offset = 999;
            Check(view.DefaultRule.Offset == 1 && view.GetRule(100).DataType == "Int32", "loaded rule ownership is independent");
            loaded = SimulatorConfiguration.Load(file); loaded.Areas[2].ItemRules[0].Address = 99;
            try { loaded.Validate(); throw new Exception("invalid rule address accepted"); } catch (ArgumentException) { Check(true, "out-of-range item rule rejected"); }
            loaded = SimulatorConfiguration.Load(file); loaded.Areas[2].DefaultRule.Operand = 0;
            try { loaded.Validate(); throw new Exception("invalid divide rule accepted"); } catch (ArgumentException) { Check(true, "invalid transformation rejected before importing"); }
            SimulatorConfiguration legacy = SimulatorConfiguration.Load(Path.Combine(Path.GetDirectoryName(args[0]), "四区联调示例.json"));
            Check(legacy.Version == 2 && legacy.DataBits == 8 && legacy.Areas[2].DefaultRule != null, "2.0 config imports with presentation defaults");
            SimulatorConfiguration demo = SimulatorConfiguration.Load(Path.Combine(Path.GetDirectoryName(args[0]), "2.1卡片解析示例.json"));
            RegisterBank demoBank = demo.CreateBank(); AreaPresentation demoView = new AreaPresentation(); demoView.Import(demo.Areas[2]);
            Check(AreaPresentation.Read(demoBank, 2, 0, demoView.GetRule(0)) == 26, "supplied Float32 engineering example");
            Check(AreaPresentation.Read(demoBank, 2, 2, demoView.GetRule(2)) == -100, "supplied signed integer example");
            Check(AreaPresentation.Read(demoBank, 2, 3, demoView.GetRule(3)) == 10000, "supplied UInt32 scaling example");
            Check(AreaPresentation.Read(demoBank, 2, 5, demoView.GetRule(5)) == 125, "supplied little-endian Float32 example");
            Check(AreaPresentation.Read(demoBank, 2, 7, demoView.GetRule(7)) == 12.5, "supplied little-endian Float64 example");
            Console.WriteLine("TOTAL " + count + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
