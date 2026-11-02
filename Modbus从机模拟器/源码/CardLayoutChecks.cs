using System;
using System.Collections.Generic;
using System.Diagnostics;
using ModbusSimulator;

// 独立模型检查，不加载 WinForms，也不写入发行 EXE。
internal static class CardLayoutChecks
{
    private static int passed;
    private static CardKey Key(int area, int address) { return new CardKey { Area = area, Address = address }; }
    private static ParseRule Rule(string type) { return new ParseRule { DataType = type }; }
    private static void Check(bool success, string name)
    { if (!success) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { Check(true, name); return; }
        throw new Exception("not rejected: " + name);
    }
    private static bool Same(CardKey value, int area, int address)
    { return value != null && value.Area == area && value.Address == address; }
    private static string Keys(IEnumerable<CardKey> values)
    {
        List<string> result = new List<string>();
        foreach (CardKey key in values) result.Add(key.Area + ":" + key.Address);
        return String.Join(",", result.ToArray());
    }
    private static RegisterBank SmallBank()
    {
        RegisterBank bank = new RegisterBank();
        bank.Configure(0, 8, 2); bank.Configure(1, 40, 1);
        bank.Configure(2, 100, 8); bank.Configure(3, 700, 3);
        return bank;
    }
    public static int Main(string[] args)
    {
        try
        {
            RegisterBank absent = new RegisterBank();
            for (int area = 0; area < 4; area++) absent.Configure(area, area + 20, 0);
            Func<int, int, ParseRule> never = delegate(int area, int address) { throw new Exception("empty areas must not resolve rules"); };
            Check(CardLayout.Build(absent, never, new CardKey[] { Key(2, 22) }).Count == 0, "all absent areas produce no cards including stale custom order");
            Reject(delegate { CardLayout.OwnerOf(absent, never, 2, 22); }, "empty register area has no owner");
            Reject(delegate { CardLayout.OwnerOf(absent, never, 0, 20); }, "empty bit area has no owner");
            absent.Configure(3, 90, 2);
            Check(Keys(CardLayout.Build(absent, delegate(int area, int address) { return Rule("UInt16"); }, null)) == "3:90,3:91", "restoring one area produces only its configured cards");
            RegisterBank bank = SmallBank();
            ParseRule word = Rule("UInt16"), dword = Rule("Float32"), qword = Rule("Float64");
            Func<int, int, ParseRule> plain = delegate(int area, int address) { return word; };
            // 非零起始和各区不同数量能够捕获按固定1000地址枚举/串区的错误。
            List<CardKey> all = CardLayout.Build(bank, plain, null);
            Check(Keys(all) == "2:100,2:101,2:102,2:103,2:104,2:105,2:106,2:107,3:700,3:701,3:702,0:8,0:9,1:40", "default order uses independent configured ranges");

            int requests = 0;
            Func<int, int, ParseRule> grouped = delegate(int area, int address)
            {
                if (area < 2) throw new Exception("bit area must not request parsing");
                if (area == 2 && (address == 101 || (address >= 103 && address <= 105)))
                    throw new Exception("hidden rules must not be consulted");
                requests++;
                if (area == 2 && address == 100) return dword;
                if (area == 2 && address == 102) return qword;
                if (area == 3 && address == 700) return dword;
                return word;
            };
            List<CardKey> merged = CardLayout.Build(bank, grouped, null);
            Check(Keys(merged) == "2:100,2:102,2:106,2:107,3:700,3:702,0:8,0:9,1:40" && requests == 6,
                "32 and 64 bit groups suppress only covered addresses");
            bank.Set(2, 101, 1234); long revision = bank.Revision;
            List<CardKey> custom = new List<CardKey> { Key(1, 40), Key(2, 102), Key(2, 101), Key(1, 40), Key(2, 999), null, Key(4, 100), Key(2, -1) };
            List<CardKey> ordered = CardLayout.Build(bank, grouped, custom);
            Check(Keys(ordered) == "1:40,2:102,2:100,2:106,2:107,3:700,3:702,0:8,0:9", "custom ordering drops hidden stale and duplicate keys");
            Check(bank.Get(2, 101) == 1234 && bank.Revision == revision && custom.Count == 8, "layout changes preserve raw values and input order");

            // 隐藏地址的独立规则保留；恢复头为16位后，该地址重新成为32位头。
            bool restored = false;
            Func<int, int, ParseRule> retained = delegate(int area, int address)
            {
                if (area == 2 && address == 100 && !restored) return qword;
                if (area == 2 && address == 101) return dword;
                return word;
            };
            List<CardKey> hidden = CardLayout.Build(bank, retained, null);
            Check(Keys(hidden).StartsWith("2:100,2:104,2:105"), "head rule overrides retained hidden address rule");
            restored = true;
            List<CardKey> revealed = CardLayout.Build(bank, retained, null);
            Check(Keys(revealed).StartsWith("2:100,2:101,2:103,2:104"), "restoring 16 bit head reveals retained child rule");
            Check(bank.Get(2, 101) == 1234 && bank.Revision == revision, "group restoration keeps register values");

            Check(Same(CardLayout.OwnerOf(bank, grouped, 2, 101), 2, 100), "owner of hidden 32 bit tail");
            Check(Same(CardLayout.OwnerOf(bank, grouped, 2, 105), 2, 102), "owner of last hidden 64 bit tail");
            Check(Same(CardLayout.OwnerOf(bank, grouped, 2, 106), 2, 106), "next independent word owns itself");
            Check(Same(CardLayout.OwnerOf(bank, grouped, 3, 701), 3, 700), "input register owner respects independent start");
            Check(Same(CardLayout.OwnerOf(bank, grouped, 0, 9), 0, 9), "bit address owns itself without decoding");
            Reject(delegate { CardLayout.OwnerOf(bank, plain, -1, 100); }, "invalid owner area rejected");
            Reject(delegate { CardLayout.OwnerOf(bank, plain, 2, 99); }, "owner below configured range rejected");
            Reject(delegate { CardLayout.OwnerOf(bank, plain, 2, 108); }, "owner above configured range rejected");

            RegisterBank tail = SmallBank(); tail.Configure(2, 65534, 2); tail.Configure(3, 77, 1);
            Func<int, int, ParseRule> wide = delegate(int area, int address) { return qword; };
            Check(Keys(CardLayout.Build(tail, wide, null)) == "2:65534,3:77,0:8,0:9,1:40", "insufficient 64 bit tail retains head without phantom cards");
            Check(Same(CardLayout.OwnerOf(tail, wide, 2, 65535), 2, 65534), "insufficient tail still maps its existing word to head");
            tail.Configure(2, 65535, 1);
            Func<int, int, ParseRule> pair = delegate(int area, int address) { return dword; };
            Check(Keys(CardLayout.Build(tail, pair, null)) == "2:65535,3:77,0:8,0:9,1:40", "insufficient 32 bit final address does not overflow");

            List<CardKey> drag = new List<CardKey> { Key(2, 100), Key(2, 102), Key(3, 700), Key(0, 8) };
            Check(Keys(CardLayout.MoveBefore(drag, Key(0, 8), Key(2, 102))) == "2:100,0:8,2:102,3:700", "drag backward inserts immediately before target");
            Check(Keys(CardLayout.MoveBefore(drag, Key(2, 100), Key(0, 8))) == "2:102,3:700,2:100,0:8", "drag forward preserves other cards order");
            Check(Keys(CardLayout.MoveBefore(drag, Key(2, 102), Key(2, 102))) == "2:100,2:102,3:700,0:8", "self drag leaves ordering unchanged");
            Check(Keys(drag) == "2:100,2:102,3:700,0:8", "drag never modifies source collection");
            Reject(delegate { CardLayout.MoveBefore(drag, Key(3, 999), Key(0, 8)); }, "missing drag source rejected");
            Reject(delegate { CardLayout.MoveBefore(drag, Key(0, 8), Key(3, 999)); }, "missing drag target rejected");
            Reject(delegate { CardLayout.MoveBefore(new List<CardKey> { Key(2, 100), Key(2, 100) }, Key(2, 100), Key(2, 100)); }, "duplicate current keys rejected even for self drag");
            Reject(delegate { CardLayout.MoveBefore(new List<CardKey> { Key(4, 1) }, Key(4, 1), Key(4, 1)); }, "invalid current key rejected");
            Reject(delegate { CardLayout.MoveBefore(new List<CardKey> { null }, Key(0, 0), Key(0, 0)); }, "null current key rejected");
            Reject(delegate { CardLayout.Build(null, plain, null); }, "null build bank rejected");
            Reject(delegate { CardLayout.Build(bank, null, null); }, "null rule resolver rejected");

            // 全地址空间确保排序和拖拽可线性处理262144张卡片。
            RegisterBank large = new RegisterBank();
            for (int area = 0; area < 4; area++) large.Configure(area, 0, 65536);
            Stopwatch timer = Stopwatch.StartNew();
            List<CardKey> maximum = CardLayout.Build(large, plain, null);
            Check(maximum.Count == 262144 && Same(maximum[0], 2, 0) && Same(maximum[65535], 2, 65535) && Same(maximum[262143], 1, 65535), "maximum four-bank layout has every unique address");
            List<CardKey> reversed = new List<CardKey>(maximum); reversed.Reverse();
            List<CardKey> reverseLayout = CardLayout.Build(large, plain, reversed);
            Check(reverseLayout.Count == 262144 && Same(reverseLayout[0], 1, 65535) && Same(reverseLayout[262143], 2, 0), "maximum custom layout preserves all requested positions");
            List<CardKey> largeDrag = CardLayout.MoveBefore(maximum, Key(1, 65535), Key(2, 0));
            Check(largeDrag.Count == 262144 && Same(largeDrag[0], 1, 65535) && Same(largeDrag[1], 2, 0) && Same(maximum[0], 2, 0), "maximum drag stable and nonmutating");
            Console.WriteLine("MAXIMUM_LAYOUT_MS " + timer.ElapsedMilliseconds);
            Console.WriteLine("TOTAL " + passed + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
