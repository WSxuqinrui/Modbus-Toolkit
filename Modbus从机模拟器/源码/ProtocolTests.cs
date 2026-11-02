using System;
using ModbusSimulator;

internal static class ProtocolTests
{
    private static byte[] Hex(string value) { string[] words = value.Split(' '); return Array.ConvertAll(words, s => Convert.ToByte(s, 16)); }
    private static void Expect(RegisterBank bank, string input, string expected)
    {
        string actual = BitConverter.ToString(bank.Process(Hex(input))).Replace('-', ' ');
        if (actual != expected) throw new Exception(input + " expected " + expected + " got " + actual);
        Console.WriteLine("PASS " + input);
    }
    public static int Main()
    {
        RegisterBank bank = new RegisterBank();
        Expect(bank, "03 00 00 00 02", "03 04 00 00 00 00");
        Expect(bank, "06 00 00 12 34", "06 00 00 12 34");
        Expect(bank, "03 00 00 00 01", "03 02 12 34");
        Expect(bank, "10 00 01 00 02 04 AB CD 00 63", "10 00 01 00 02");
        Expect(bank, "03 00 00 00 03", "03 06 12 34 AB CD 00 63");
        Expect(bank, "05 00 00 FF 00", "05 00 00 FF 00");
        Expect(bank, "01 00 00 00 09", "01 02 01 00");
        Expect(bank, "0F 00 01 00 09 02 AA 01", "0F 00 01 00 09");
        Expect(bank, "01 00 00 00 0A", "01 02 55 03");
        bank.Set(1, 8, 1); bank.Set(3, 0, 0xA123);
        Expect(bank, "02 00 08 00 01", "02 01 01");
        Expect(bank, "04 00 00 00 01", "04 02 A1 23");
        Expect(bank, "03 03 E7 00 01", "03 02 00 00");
        Expect(bank, "03 03 E7 00 02", "83 02");
        Expect(bank, "03 FF FF 00 02", "83 02");
        Expect(bank, "03 00 00 00 00", "83 03");
        Expect(bank, "03 00 00 00 7E", "83 03");
        Expect(bank, "05 00 00 00 01", "85 03");
        Expect(bank, "10 00 00 00 02 02 FF FF", "90 03");
        Expect(bank, "03 00 00 00 01", "03 02 12 34");
        Expect(bank, "10 00 00 00 7C 00", "90 03");
        Expect(bank, "03 00", "83 03");
        Expect(bank, "17 00 00 00 01", "97 01");
        if (RegisterBank.Crc(Hex("01 03 00 00 00 0A"), 6) != 0xCDC5) throw new Exception("CRC reference mismatch");
        Console.WriteLine("PASS CRC reference; TOTAL 23 checks");
        return 0;
    }
}
