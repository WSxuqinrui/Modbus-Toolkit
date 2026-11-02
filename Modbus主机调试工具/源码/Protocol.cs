using System;

namespace ModbusMaster
{
    // 四种对象独立存储；按绝对协议地址访问，数组只分配已配置区间。
    public sealed class RegisterBank
    {
        public const int Size = 1000;
        public const int Capacity = 65536;
        private readonly ushort[][] data = { new ushort[Size], new ushort[Size], new ushort[Size], new ushort[Size] };
        private readonly int[] starts = new int[4];
        private readonly object gate = new object();
        private long revision;
        public long Revision { get { lock (gate) return revision; } }
        private static void CheckArea(int area) { if (area < 0 || area > 3) throw new ArgumentOutOfRangeException("area"); }
        public int Start(int area) { CheckArea(area); lock (gate) return starts[area]; }
        public int Count(int area) { CheckArea(area); lock (gate) return data[area].Length; }
        public ushort Get(int area, int address)
        {
            CheckArea(area);
            lock (gate)
            {
                if (!InRange(area, address, 1)) throw new ArgumentOutOfRangeException("address");
                return data[area][address - starts[area]];
            }
        }
        public void Configure(int area, int start, int count)
        {
            CheckArea(area);
            if (start < 0 || start >= Capacity || count < 0 || count > Capacity - start)
                throw new ArgumentOutOfRangeException("count", "起始地址必须在0～65535，数量不得为负；起始地址 + 数量不得超过65536。数量0表示该类不存在。");
            lock (gate)
            {
                ushort[] replacement = new ushort[count];
                int first = Math.Max(start, starts[area]);
                int end = Math.Min(start + count, starts[area] + data[area].Length);
                if (end > first) Array.Copy(data[area], first - starts[area], replacement, first - start, end - first);
                data[area] = replacement;
                starts[area] = start;
                revision++;
            }
        }
        public void Set(int area, int address, ushort value)
        {
            CheckArea(area);
            if (area < 2 && value > 1) throw new ArgumentException("位对象仅允许 0 或 1");
            lock (gate)
            {
                if (!InRange(area, address, 1)) throw new ArgumentOutOfRangeException("address");
                data[area][address - starts[area]] = value;
                revision++;
            }
        }
        public void Fill(int area, ushort first, bool increment)
        {
            CheckArea(area);
            if (area < 2 && first > 1) throw new ArgumentException("位对象仅允许 0 或 1");
            lock (gate)
            {
                for (int i = 0; i < data[area].Length; i++)
                    data[area][i] = increment ? (ushort)((first + i) % (area < 2 ? 2 : Capacity)) : first;
                revision++;
            }
        }
        // 同一锁内完成多寄存器操作，浮点值不会被主站读到只写了一半的状态。
        public ushort[] ReadWords(int area, int address, int count)
        {
            CheckArea(area);
            lock (gate)
            {
                if (count < 1 || !InRange(area, address, count)) throw new ArgumentOutOfRangeException("count", "解析需要的尾部寄存器不足。");
                ushort[] result = new ushort[count]; Array.Copy(data[area], address - starts[area], result, 0, count); return result;
            }
        }
        public void WriteWords(int area, int address, ushort[] words)
        {
            CheckArea(area);
            if (words == null || words.Length == 0) throw new ArgumentException("写入数据不能为空。");
            ushort[] copy = (ushort[])words.Clone();
            if (area < 2) foreach (ushort value in copy) if (value > 1) throw new ArgumentException("位值必须为0或1。");
            lock (gate)
            {
                if (!InRange(area, address, copy.Length)) throw new ArgumentOutOfRangeException("address", "多寄存器写入越过数据区末尾。");
                Array.Copy(copy, 0, data[area], address - starts[area], copy.Length); revision++;
            }
        }
        public AreaConfiguration[] Snapshot()
        {
            lock (gate)
            {
                AreaConfiguration[] result = new AreaConfiguration[4];
                for (int area = 0; area < 4; area++)
                    result[area] = new AreaConfiguration { Start = starts[area], Count = data[area].Length, Values = (ushort[])data[area].Clone() };
                return result;
            }
        }
        public static int Word(byte[] bytes, int index) { return (bytes[index] << 8) | bytes[index + 1]; }
        private static byte[] Error(byte function, byte code) { return new byte[] { (byte)(function | 0x80), code }; }
        private bool InRange(int area, int address, int count)
        { return address >= starts[area] && address - starts[area] < data[area].Length && count <= data[area].Length - (address - starts[area]); }

        // 消费与产生 PDU；传输层负责 MBAP 或站号/CRC，不混用两种封装。
        public byte[] Process(byte[] pdu)
        {
            if (pdu == null || pdu.Length == 0) return null;
            byte function = pdu[0];
            if (function != 1 && function != 2 && function != 3 && function != 4 &&
                function != 5 && function != 6 && function != 15 && function != 16) return Error(function, 1);
            if (pdu.Length < 5) return Error(function, 3);
            int address = Word(pdu, 1), argument = Word(pdu, 3);
            lock (gate)
            {
                if (function >= 1 && function <= 4)
                {
                    if (pdu.Length != 5 || argument < 1 || argument > (function < 3 ? 2000 : 125)) return Error(function, 3);
                    int area = function == 1 ? 0 : function == 2 ? 1 : function == 3 ? 2 : 3;
                    if (!InRange(area, address, argument)) return Error(function, 2);
                    int length = function < 3 ? (argument + 7) / 8 : argument * 2;
                    byte[] reply = new byte[length + 2];
                    reply[0] = function; reply[1] = (byte)length;
                    for (int i = 0; i < argument; i++)
                    {
                        ushort value = data[area][address - starts[area] + i];
                        if (function < 3) { if (value != 0) reply[2 + i / 8] |= (byte)(1 << (i % 8)); }
                        else { reply[2 + i * 2] = (byte)(value >> 8); reply[3 + i * 2] = (byte)value; }
                    }
                    return reply;
                }
                if (function == 5 || function == 6)
                {
                    if (pdu.Length != 5 || (function == 5 && argument != 0 && argument != 0xFF00)) return Error(function, 3);
                    int area = function == 5 ? 0 : 2;
                    if (!InRange(area, address, 1)) return Error(function, 2);
                    data[area][address - starts[area]] = (ushort)(function == 5 ? (argument == 0 ? 0 : 1) : argument);
                    revision++;
                    return (byte[])pdu.Clone();
                }
                if (pdu.Length < 6 || argument < 1 || argument > (function == 15 ? 1968 : 123)) return Error(function, 3);
                int expected = function == 15 ? (argument + 7) / 8 : argument * 2;
                if (pdu[5] != expected || pdu.Length != expected + 6) return Error(function, 3);
                int writeArea = function == 15 ? 0 : 2;
                if (!InRange(writeArea, address, argument)) return Error(function, 2);
                // 校验完整请求之后统一提交，非法报文不会造成部分写入。
                for (int i = 0; i < argument; i++)
                    data[writeArea][address - starts[writeArea] + i] = (ushort)(function == 15 ? ((pdu[6 + i / 8] >> (i % 8)) & 1) : Word(pdu, 6 + i * 2));
                revision++;
                return new byte[] { function, pdu[1], pdu[2], pdu[3], pdu[4] };
            }
        }

        public static ushort Crc(byte[] bytes, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++) crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }
    }
}
