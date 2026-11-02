using System;
using System.Text;

namespace ModbusMaster
{
    /// <summary>纯字节编排回归，不打开串口、网络或主窗口。</summary>
    public static class RawPayloadChecks
    {
        private static int checks;

        public static int Main(string[] args)
        {
            try
            {
                Run();
                Console.WriteLine("RawPayloadChecks PASS " + checks);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("RawPayloadChecks FAIL: " + error.Message);
                return 1;
            }
        }

        public static void Run()
        {
            checks = 0;
            Bytes(new byte[] { 1, 2, 0xA0, 0xFF }, RawPayload.Compose("0102a0FF", true, 0, true, 0), "continuous HEX");
            Bytes(new byte[] { 1, 2, 0xA0, 0xFF }, RawPayload.Compose(" 0x01,02\t0Xa0\r\nFF, ", true, 0, true, 0), "HEX whitespace comma and prefixes");
            Bytes(new byte[] { 1, 2 }, RawPayload.Compose("0x0102", true, 0, true, 0), "prefixed continuous HEX");
            Bytes(new byte[] { 0, 0x7F }, RawPayload.Compose("\0\x7F", false, 0, true, 0), "ASCII includes NUL and DEL");
            Bytes(new byte[] { 0x41, 13, 10, 0x42 }, RawPayload.Compose("A\r\nB", false, 0, true, 0), "ASCII preserves actual line breaks");
            Bytes(new byte[] { 0x5C, 0x72, 0x5C, 0x6E }, RawPayload.Compose("\\r\\n", false, 0, true, 0), "ASCII backslashes are literal");
            Bytes(new byte[] { 32 }, RawPayload.Compose(" ", false, 0, true, 0), "ASCII space is data");
            Bytes(new byte[] { 1 }, RawPayload.Compose("01", true, 0, false, 0), "no checksum adds no byte");
            Bytes(new byte[] { 1, 13 }, RawPayload.Compose("01", true, 0, true, 1), "CR suffix");
            Bytes(new byte[] { 1, 10 }, RawPayload.Compose("01", true, 0, true, 2), "LF suffix");
            Bytes(new byte[] { 1, 13, 10 }, RawPayload.Compose("01", true, 0, true, 3), "CRLF suffix");
            Bytes(new byte[] { 1, 2, 3, 13, 10 }, RawPayload.Compose("0102", true, 6, true, 3), "SUM8 precedes newline and excludes it");
            Bytes(new byte[] { 1, 2, 0xFD }, RawPayload.Compose("0102", true, 5, true, 0), "LRC is twos complement");
            Bytes(new byte[] { 0xFF, 2, 1 }, RawPayload.Compose("FF02", true, 6, true, 0), "SUM8 wraps");
            Bytes(new byte[] { 0xFF, 2, 0xFD }, RawPayload.Compose("FF02", true, 7, false, 0), "XOR8");
            Bytes(new byte[] { 1, 3, 0, 0, 0, 10, 0xC5, 0xCD }, RawPayload.Compose("01 03 00 00 00 0A", true, 1, true, 0), "Modbus read request literal");

            // 固定向量来源：CRC RevEng catalogue，check 是 ASCII "123456789" 的 CRC。
            // https://reveng.sourceforge.io/crc-catalogue/16.htm#crc.cat.crc-16-modbus
            // https://reveng.sourceforge.io/crc-catalogue/16.htm#crc.cat.crc-16-ibm-3740
            // https://reveng.sourceforge.io/crc-catalogue/16.htm#crc.cat.crc-16-xmodem
            // https://reveng.sourceforge.io/crc-catalogue/17plus.htm#crc.cat.crc-32-iso-hdlc
            // 测试只比较独立字面量，不复制生产 CRC 算法生成期望值。
            Vector(1, new byte[] { 0x37, 0x4B }, new byte[] { 0x4B, 0x37 }, "CRC-16/Modbus 4B37");
            Vector(2, new byte[] { 0xB1, 0x29 }, new byte[] { 0x29, 0xB1 }, "CRC-16/CCITT-FALSE 29B1");
            Vector(3, new byte[] { 0xC3, 0x31 }, new byte[] { 0x31, 0xC3 }, "CRC-16/XMODEM 31C3");
            Vector(4, new byte[] { 0x26, 0x39, 0xF4, 0xCB }, new byte[] { 0xCB, 0xF4, 0x39, 0x26 }, "CRC-32/ISO-HDLC CBF43926");
            Vector(5, new byte[] { 0x23 }, new byte[] { 0x23 }, "LRC 23");
            Vector(6, new byte[] { 0xDD }, new byte[] { 0xDD }, "SUM8 DD");
            Vector(7, new byte[] { 0x31 }, new byte[] { 0x31 }, "XOR8 31");
            Vector(8, new byte[] { 0xF4 }, new byte[] { 0xF4 }, "CRC-8/SMBUS F4");
            Vector(9, new byte[] { 0xA1 }, new byte[] { 0xA1 }, "CRC-8/MAXIM-DOW A1");
            Vector(10, new byte[] { 0x3D, 0xBB }, new byte[] { 0xBB, 0x3D }, "CRC-16/ARC BB3D");
            Vector(11, new byte[] { 0xC8, 0xB4 }, new byte[] { 0xB4, 0xC8 }, "CRC-16/USB B4C8");
            Vector(12, new byte[] { 0x82, 0xEA }, new byte[] { 0xEA, 0x82 }, "CRC-16/DNP EA82");
            Vector(13, new byte[] { 0xDD, 0x01 }, new byte[] { 0x01, 0xDD }, "SUM16 01DD");

            string[] invalidHex = { "", " ", ",\r\n", "0", "123", "0 1", "0x", "0x1", "0xGG", "GG", "01-02", "01:02", "010x02", "0x0x01", "01中" };
            foreach (string input in invalidHex)
            {
                string value = input;
                Reject(delegate { RawPayload.Compose(value, true, 0, true, 0); }, "invalid HEX: " + value);
            }
            Reject(delegate { RawPayload.Compose(null, true, 0, true, 0); }, "null input");
            Reject(delegate { RawPayload.Compose("", false, 0, true, 0); }, "empty ASCII");
            Reject(delegate { RawPayload.Compose("A中", false, 0, true, 0); }, "non ASCII character");
            Reject(delegate { RawPayload.Compose("\x80", false, 0, true, 0); }, "ASCII upper boundary");
            Reject(delegate { RawPayload.Compose("01", true, -1, true, 0); }, "negative checksum index");
            Reject(delegate { RawPayload.Compose("01", true, 14, true, 0); }, "large checksum index");
            Reject(delegate { RawPayload.Compose("01", true, 0, true, -1); }, "negative newline index");
            Reject(delegate { RawPayload.Compose("01", true, 0, true, 4); }, "large newline index");

            Check(RawPayload.Compose(new string('A', 8192), false, 0, true, 0).Length == 8192, "ASCII maximum payload");
            Check(RawPayload.Compose(new string('0', 16384), true, 0, true, 0).Length == 8192, "HEX maximum payload");
            Check(RawPayload.Compose(new string('A', 8191), false, 6, true, 0).Length == 8192, "checksum included in maximum");
            Check(RawPayload.Compose(new string('A', 8186), false, 4, true, 3).Length == 8192, "CRC32 and CRLF included in maximum");
            Reject(delegate { RawPayload.Compose(new string('A', 8193), false, 0, true, 0); }, "ASCII over maximum");
            Reject(delegate { RawPayload.Compose(new string('0', 16386), true, 0, true, 0); }, "HEX over maximum");
            Reject(delegate { RawPayload.Compose(new string('A', 8192), false, 6, true, 0); }, "checksum pushes over maximum");
            Reject(delegate { RawPayload.Compose(new string('A', 8187), false, 4, true, 3); }, "CRC32 plus newline pushes over maximum");
        }

        private static void Vector(int checksum, byte[] low, byte[] high, string name)
        {
            byte[] input = Encoding.ASCII.GetBytes("123456789");
            byte[] expectedLow = new byte[input.Length + low.Length];
            byte[] expectedHigh = new byte[input.Length + high.Length];
            Array.Copy(input, expectedLow, input.Length); Array.Copy(low, 0, expectedLow, input.Length, low.Length);
            Array.Copy(input, expectedHigh, input.Length); Array.Copy(high, 0, expectedHigh, input.Length, high.Length);
            Bytes(expectedLow, RawPayload.Compose("123456789", false, checksum, true, 0), name + " low first");
            Bytes(expectedHigh, RawPayload.Compose("313233343536373839", true, checksum, false, 0), name + " high first HEX");
        }

        private static void Bytes(byte[] expected, byte[] actual, string name)
        {
            Check(actual != null && BitConverter.ToString(expected) == BitConverter.ToString(actual), name);
        }

        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
        }

        private static void Reject(Action action, string name)
        {
            try { action(); }
            catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException("Expected rejection: " + name);
        }
    }
}
