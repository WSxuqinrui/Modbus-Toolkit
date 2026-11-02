using System;
using System.IO;
using ModbusSimulator;

internal static class ModelChecks
{
    private static int passed;
    private static byte[] Hex(string value) { return Array.ConvertAll(value.Split(' '), s => Convert.ToByte(s, 16)); }
    private static void Check(bool success, string name)
    { if (!success) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static void Expect(RegisterBank bank, string request, string expected)
    { Check(BitConverter.ToString(bank.Process(Hex(request))).Replace('-', ' ') == expected, request); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { Check(true, name); return; }
        throw new Exception("not rejected: " + name);
    }
    public static int Main(string[] args)
    {
        try
        {
            RegisterBank empty = new RegisterBank();
            empty.Set(2, 2, 5678); empty.Configure(2, 0, 0);
            Check(empty.Count(2) == 0 && empty.Snapshot()[2].Values.Length == 0, "nonempty to zero removes all configured values");
            Reject(delegate { empty.Get(2, 0); }, "empty area get rejected");
            Reject(delegate { empty.Set(2, 0, 1); }, "empty area set rejected");
            Reject(delegate { empty.ReadWords(2, 0, 1); }, "empty area read words rejected");
            Reject(delegate { empty.WriteWords(2, 0, new ushort[] { 1 }); }, "empty area write words rejected");
            for (int area = 0; area < 4; area++) empty.Configure(area, 0, 0);
            Expect(empty, "01 00 00 00 01", "81 02");
            Expect(empty, "02 00 00 00 01", "82 02");
            Expect(empty, "03 00 00 00 01", "83 02");
            Expect(empty, "04 00 00 00 01", "84 02");
            Expect(empty, "05 00 00 FF 00", "85 02");
            Expect(empty, "06 00 00 00 01", "86 02");
            Expect(empty, "0F 00 00 00 01 01 01", "8F 02");
            Expect(empty, "10 00 00 00 01 02 00 01", "90 02");
            empty.Configure(2, 0, 4);
            Check(empty.Get(2, 2) == 0 && empty.Count(2) == 4, "zero to nonempty creates fresh values without restoring deleted data");
            empty.Configure(2, 65535, 0);
            Check(empty.Start(2) == 65535 && empty.Count(2) == 0, "empty range permits maximum legal start");
            Reject(delegate { empty.Configure(2, 65536, 0); }, "empty range still rejects out of space start");
            Reject(delegate { empty.Configure(2, -1, 0); }, "empty range still rejects negative start");
            Reject(delegate { empty.Configure(2, 0, -1); }, "negative count rejected");

            RegisterBank bank = new RegisterBank();
            bank.Set(2, 100, 321);
            bank.Configure(2, 100, 3);
            Check(bank.Start(2) == 100 && bank.Count(2) == 3 && bank.Get(2, 100) == 321, "configure preserves overlapping address");
            Expect(bank, "03 00 64 00 03", "03 06 01 41 00 00 00 00");
            Expect(bank, "03 00 63 00 01", "83 02");
            Expect(bank, "03 00 66 00 02", "83 02");
            Expect(bank, "06 00 65 FF FF", "06 00 65 FF FF");
            Check(bank.Get(2, 101) == 65535, "write into custom range");
            bank.Configure(3, 65535, 1); bank.Set(3, 65535, 4660);
            Expect(bank, "04 FF FF 00 01", "04 02 12 34");
            Expect(bank, "04 FF FF 00 02", "84 02");
            bank.Configure(0, 0, 65536); bank.Set(0, 65535, 1);
            Expect(bank, "01 FF FF 00 01", "01 01 01");
            Check(bank.Get(1, 0) == 0, "same address in distinct objects independent");
            Reject(delegate { bank.Configure(2, 65535, 2); }, "address space overflow rejected");
            Reject(delegate { bank.Configure(2, -1, 1); }, "negative address rejected");
            Reject(delegate { bank.Configure(2, 0, 65537); }, "oversized count rejected");
            Check(bank.Start(2) == 100 && bank.Count(2) == 3, "failed configure leaves definition intact");
            bank.Fill(2, 65534, true);
            Check(bank.Get(2, 100) == 65534 && bank.Get(2, 101) == 65535 && bank.Get(2, 102) == 0, "increment fill wraps uint16");
            bank.Fill(0, 1, true);
            Check(bank.Get(0, 0) == 1 && bank.Get(0, 1) == 0 && bank.Get(0, 65535) == 0, "bit increment fill alternates");
            Reject(delegate { bank.Set(0, 0, 2); }, "bit value validation");
            Reject(delegate { bank.Set(2, 99, 1); }, "UI setter respects configured range");
            bank.Configure(2, 102, 4);
            Check(bank.Get(2, 102) == 0 && bank.Get(2, 105) == 0, "resizing by absolute address");
            bank.Set(2, 102, 4321);
            string path = Path.Combine(args[0], "configuration-test.json");
            SimulatorConfiguration config = SimulatorConfiguration.Capture(bank);
            config.Mode = 2; config.Unit = 7; config.Port = 1502; config.Com = "COM_TEST";
            config.Save(path);
            SimulatorConfiguration loaded = SimulatorConfiguration.Load(path);
            RegisterBank restored = loaded.CreateBank();
            Check(loaded.Mode == 2 && loaded.Unit == 7 && loaded.Port == 1502 && loaded.Com == "COM_TEST", "connection settings roundtrip");
            Check(restored.Start(2) == 102 && restored.Count(2) == 4 && restored.Get(2, 102) == 4321 && restored.Get(3, 65535) == 4660, "all bank ranges and values roundtrip");
            loaded.Areas[0].Values[0] = 2;
            Reject(delegate { loaded.CreateBank(); }, "bad bit value rejected before import");
            Check(bank.Get(2, 102) == 4321, "invalid import does not mutate active bank");
            loaded = SimulatorConfiguration.Load(path); loaded.Areas[2].Count = 3;
            Reject(delegate { loaded.CreateBank(); }, "mismatched data count rejected");
            loaded = SimulatorConfiguration.Load(path); loaded.Unit = 0;
            Reject(delegate { loaded.Validate(); }, "invalid station rejected");
            ushort number;
            Check(ValueParser.TryParse("0xFFFF", false, out number) && number == 65535, "hex edit");
            Check(ValueParser.TryParse("-1", true, out number) && number == 65535, "signed edit maps to raw bits");
            Check(!ValueParser.TryParse("-1", false, out number) && !ValueParser.TryParse("65536", false, out number), "invalid unsigned edit rejected");
            Check(ValueParser.TryParse("-32768", true, out number) && number == 32768 && !ValueParser.TryParse("-32769", true, out number), "signed edit boundary");
            RegisterBank full = new RegisterBank();
            for (int i = 0; i < 4; i++) { full.Configure(i, 0, 65536); full.Fill(i, (ushort)(i < 2 ? 1 : 65535), false); }
            string fullPath = Path.Combine(args[0], "full-size-test.json");
            SimulatorConfiguration.Capture(full).Save(fullPath);
            RegisterBank fullRestored = SimulatorConfiguration.Load(fullPath).CreateBank();
            Check(fullRestored.Count(0) == 65536 && fullRestored.Count(3) == 65536 && fullRestored.Get(2, 65535) == 65535 && fullRestored.Get(0, 65535) == 1, "maximum four banks configuration roundtrip");
            SimulatorConfiguration.Capture(bank).Save(path);
            Check(SimulatorConfiguration.Load(path).CreateBank().Get(2, 102) == 4321, "replacing existing saved configuration");
            Console.WriteLine("TOTAL " + passed + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
