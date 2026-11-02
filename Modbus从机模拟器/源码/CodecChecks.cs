using System;
using ModbusSimulator;

// 独立宿主检查：csc /out:CodecChecks.exe ValueCodec.cs CodecChecks.cs
internal static class CodecChecks
{
    private static int passed;
    private static ParseRule Rule(string type, string order)
    { return new ParseRule { DataType = type, ByteOrder = order }; }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static void Equal(double expected, double actual, string name)
    { Check(expected == actual, name + " expected=" + expected + " actual=" + actual); }
    private static void Words(ushort[] expected, ushort[] actual, string name)
    {
        Check(expected.Length == actual.Length, name + " length");
        for (int i = 0; i < expected.Length; i++) Check(expected[i] == actual[i], name + " word " + i);
    }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { Check(true, name); return; }
        throw new Exception("not rejected: " + name);
    }
    public static int Main()
    {
        try
        {
            Equal(65535, ValueCodec.Decode(new ushort[] { 0xFFFF }, Rule("UInt16", "ABCD")), "UInt16 maximum");
            Equal(-32768, ValueCodec.Decode(new ushort[] { 0x8000 }, Rule("Int16", "ABCD")), "Int16 minimum");
            Equal(4294967295d, ValueCodec.Decode(new ushort[] { 0xFFFF, 0xFFFF }, Rule("UInt32", "ABCD")), "UInt32 maximum");
            Equal(-2147483648d, ValueCodec.Decode(new ushort[] { 0x8000, 0x0000 }, Rule("Int32", "ABCD")), "Int32 minimum");
            string[] orders = { "ABCD", "BADC", "CDAB", "DCBA" };
            ushort[][] literal32 = {
                new ushort[] { 0x3F80, 0x0000 }, new ushort[] { 0x803F, 0x0000 },
                new ushort[] { 0x0000, 0x3F80 }, new ushort[] { 0x0000, 0x803F }
            };
            ushort[][] literal64 = {
                new ushort[] { 0x3FF0, 0, 0, 0 }, new ushort[] { 0xF03F, 0, 0, 0 },
                new ushort[] { 0, 0, 0, 0x3FF0 }, new ushort[] { 0, 0, 0, 0xF03F }
            };
            // 非零各字节字面量避免仅用 1.0 时遗漏字顺序错误。
            ushort[][] pattern64 = {
                new ushort[] { 0x3FF1, 0x2233, 0x4455, 0x6677 },
                new ushort[] { 0xF13F, 0x3322, 0x5544, 0x7766 },
                new ushort[] { 0x6677, 0x4455, 0x2233, 0x3FF1 },
                new ushort[] { 0x7766, 0x5544, 0x3322, 0xF13F }
            };
            double pattern = BitConverter.Int64BitsToDouble(unchecked((long)0x3FF1223344556677UL));
            for (int i = 0; i < orders.Length; i++)
            {
                ParseRule f32 = Rule("Float32", orders[i]), f64 = Rule("Float64", orders[i]);
                Equal(1, ValueCodec.Decode(literal32[i], f32), "IEEE float 1.0 " + orders[i]);
                Words(literal32[i], ValueCodec.Encode(1, f32), "encode float 1.0 " + orders[i]);
                Equal(1, ValueCodec.Decode(literal64[i], f64), "IEEE double 1.0 " + orders[i]);
                Words(literal64[i], ValueCodec.Encode(1, f64), "encode double 1.0 " + orders[i]);
                Equal(pattern, ValueCodec.Decode(pattern64[i], f64), "double all bytes " + orders[i]);
                Words(pattern64[i], ValueCodec.Encode(pattern, f64), "encode double all bytes " + orders[i]);
            }
            Equal(12.5, ValueCodec.Decode(new ushort[] { 0x4148, 0 }, Rule("Float32", "ABCD")), "float 12.5");
            Equal(-12.5, ValueCodec.Decode(new ushort[] { 0xC148, 0 }, Rule("Float32", "ABCD")), "float negative");
            Equal(12.5, ValueCodec.Decode(new ushort[] { 0x4029, 0, 0, 0 }, Rule("Float64", "ABCD")), "double 12.5");
            Words(new ushort[] { 0xFFFF, 0xFFFF }, ValueCodec.Encode(4294967295d, Rule("UInt32", "ABCD")), "encode UInt32 maximum");
            Words(new ushort[] { 0x8000, 0 }, ValueCodec.Encode(-2147483648d, Rule("Int32", "ABCD")), "encode Int32 minimum");
            string[] ops = { "+", "-", "*", "/" };
            double[] expected = { 24, 20, 37, 14.5 };
            for (int i = 0; i < ops.Length; i++)
            {
                ParseRule r = Rule("Int16", "ABCD"); r.Operation = ops[i]; r.Operand = 2; r.Offset = 7;
                Equal(expected[i], ValueCodec.Decode(new ushort[] { 15 }, r), "operation " + ops[i]);
                Words(new ushort[] { 15 }, ValueCodec.Encode(expected[i], r), "inverse " + ops[i]);
            }
            Check(ValueCodec.WordCount(Rule("Float64", "ABCD")) == 4, "Float64 count");
            Reject(delegate { ValueCodec.Decode(new ushort[] { 1 }, Rule("Float32", "ABCD")); }, "missing tail");
            Reject(delegate { ValueCodec.Encode(1.25, Rule("Int16", "ABCD")); }, "fractional integer");
            Reject(delegate { ValueCodec.Encode(-1, Rule("UInt16", "ABCD")); }, "negative unsigned");
            Reject(delegate { ValueCodec.Encode(65536, Rule("UInt16", "ABCD")); }, "UInt16 overflow");
            Reject(delegate { ValueCodec.Encode(4294967296d, Rule("UInt32", "ABCD")); }, "UInt32 overflow");
            Reject(delegate { ValueCodec.Encode(2147483648d, Rule("Int32", "ABCD")); }, "Int32 overflow");
            Reject(delegate { ValueCodec.Encode(double.NaN, Rule("Float64", "ABCD")); }, "NaN write");
            Reject(delegate { ValueCodec.Encode(double.PositiveInfinity, Rule("Float64", "ABCD")); }, "Infinity write");
            Reject(delegate { ValueCodec.Encode(double.MaxValue, Rule("Float32", "ABCD")); }, "Float32 overflow");
            Reject(delegate { new ParseRule { Operation = "/", Operand = 0 }.Validate(); }, "divide zero rule");
            Reject(delegate { new ParseRule { Operation = "*", Operand = 0 }.Validate(); }, "noninvertible zero multiplier");
            Reject(delegate { new ParseRule { Operand = double.NaN }.Validate(); }, "NaN operand");
            Reject(delegate { new ParseRule { Offset = double.PositiveInfinity }.Validate(); }, "Infinity offset");
            Reject(delegate { new ParseRule { DataType = "UInt64" }.Validate(); }, "invalid type");
            Reject(delegate { new ParseRule { ByteOrder = "ACBD" }.Validate(); }, "invalid order");
            Reject(delegate { new ParseRule { Operation = "^" }.Validate(); }, "invalid operation");
            Check(double.IsNaN(ValueCodec.Decode(new ushort[] { 0x7FC0, 0 }, Rule("Float32", "ABCD"))), "NaN readable for explicit display");
            Check(double.IsPositiveInfinity(ValueCodec.Decode(new ushort[] { 0x7FF0, 0, 0, 0 }, Rule("Float64", "ABCD"))), "Infinity readable for explicit display");
            ParseRule decimalScale = new ParseRule { DataType = "UInt16", Operation = "*", Operand = 0.1 };
            Words(new ushort[] { 3 }, ValueCodec.Encode(0.3, decimalScale), "decimal scale inverse accepts arithmetic rounding");
            double unchanged = ValueCodec.Decode(new ushort[] { 3 }, decimalScale);
            Words(new ushort[] { 3 }, ValueCodec.Encode(unchanged, decimalScale), "decimal scale unchanged edit roundtrip");
            ParseRule cancellation = new ParseRule { DataType = "UInt16", Operation = "+", Operand = 0.1, Offset = 0.2 };
            double zeroEngineering = ValueCodec.Decode(new ushort[] { 0 }, cancellation);
            Words(new ushort[] { 0 }, ValueCodec.Encode(zeroEngineering, cancellation), "zero cancellation unchanged edit roundtrip");
            Reject(delegate { ValueCodec.Encode(zeroEngineering + 1e-10, cancellation); }, "zero cancellation actual fraction rejected");
            ParseRule lowerEdge = new ParseRule { DataType = "UInt16", Operation = "+", Operand = 0.1, Offset = 0.7 };
            double lowerDisplay = ValueCodec.Decode(new ushort[] { 0 }, lowerEdge);
            Words(new ushort[] { 0 }, ValueCodec.Encode(lowerDisplay, lowerEdge), "lower boundary negative residual roundtrip");
            ParseRule upperEdge = new ParseRule { DataType = "UInt16", Operation = "+", Operand = 0.2, Offset = 1.1 };
            double upperDisplay = ValueCodec.Decode(new ushort[] { 65535 }, upperEdge);
            Words(new ushort[] { 65535 }, ValueCodec.Encode(upperDisplay, upperEdge), "upper boundary positive residual roundtrip");
            double belowLower = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(lowerDisplay) - 1);
            double aboveUpper = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(upperDisplay) + 1);
            Reject(delegate { ValueCodec.Encode(belowLower, lowerEdge); }, "engineering one ULP below lower boundary rejected");
            Reject(delegate { ValueCodec.Encode(aboveUpper, upperEdge); }, "engineering one ULP above upper boundary rejected");
            ParseRule collapsed = new ParseRule { DataType = "UInt16", Operation = "+", Operand = 0.1, Offset = 1e20 };
            Reject(delegate { ValueCodec.Encode(ValueCodec.Decode(new ushort[] { 0 }, collapsed), collapsed); }, "collapsed boundary transformation rejected");
            Reject(delegate { ValueCodec.Encode(ValueCodec.Decode(new ushort[] { 123 }, collapsed), collapsed); }, "collapsed interior transformation rejected");
            Reject(delegate { ValueCodec.Encode(3.0000000001, Rule("UInt16", "ABCD")); }, "actual small fraction rejected");
            Reject(delegate { ValueCodec.Encode(3.0000000000000004, Rule("UInt16", "ABCD")); }, "untransformed fractional ULP rejected");
            Reject(delegate { ValueCodec.Encode(0.30000000001, decimalScale); }, "scaled actual small fraction rejected");
            ParseRule largeScale = new ParseRule { DataType = "UInt32", Operation = "*", Operand = 0.1 };
            Reject(delegate { ValueCodec.Encode(429496729.49999994d, largeScale); }, "scaled UInt32 fraction exceeds absolute tolerance");
            Reject(delegate { ValueCodec.Encode(4294967294.9999995d, Rule("UInt32", "ABCD")); }, "UInt32 near maximum fraction rejected");
            Reject(delegate { ValueCodec.Encode(4294967295.0000005d, Rule("UInt32", "ABCD")); }, "UInt32 one ULP overflow rejected");
            Reject(delegate { ValueCodec.Encode(-2147483648.0000005d, Rule("Int32", "ABCD")); }, "Int32 one ULP underflow rejected");
            Console.WriteLine("ALL PASS " + passed); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
}
