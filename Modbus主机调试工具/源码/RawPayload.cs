using System;
using System.Collections.Generic;

namespace ModbusMaster
{
    /// <summary>原始字节输入 → 校验 → 换行；不增加任何 Modbus 帧头或转义编码。</summary>
    public static class RawPayload
    {
        public static readonly string[] ChecksumNames = {
            "无校验", "CRC-16/Modbus", "CRC-16/CCITT-FALSE", "CRC-16/XMODEM",
            "CRC-32/ISO-HDLC", "LRC", "SUM8", "XOR8", "CRC-8/SMBUS", "CRC-8/MAXIM-DOW",
            "CRC-16/ARC (IBM)", "CRC-16/USB", "CRC-16/DNP", "SUM16"
        };
        private const int MaximumLength = 8192;

        /// <summary>
        /// HEX 可连续输入或以空白、逗号分隔，每个片段可带 0x 前缀且必须为偶数位。
        /// ASCII 仅接受 U+0000～U+007F，保留文本框实际换行；不解释反斜线。
        /// lowByteFirst 只改变多字节 CRC 的附加顺序；newlineIndex 为无/CR/LF/CRLF。
        /// </summary>
        public static byte[] Compose(string input, bool hex, int checksumIndex, bool lowByteFirst, int newlineIndex)
        {
            if (input == null) throw new ArgumentNullException("input", "请输入要发送的原始字节。");
            if (checksumIndex < 0 || checksumIndex >= ChecksumNames.Length) throw new ArgumentOutOfRangeException("checksumIndex", "校验选项无效。");
            if (newlineIndex < 0 || newlineIndex > 3) throw new ArgumentOutOfRangeException("newlineIndex", "换行选项无效。");
            List<byte> bytes = hex ? ParseHex(input) : ParseAscii(input);
            if (bytes.Count == 0) throw new ArgumentException("请输入至少一个原始字节。", "input");

            int checksumLength = ChecksumLength(checksumIndex);
            int newlineLength = newlineIndex == 0 ? 0 : newlineIndex == 3 ? 2 : 1;
            if (bytes.Count + checksumLength + newlineLength > MaximumLength)
                throw new ArgumentException("输入、校验及换行合计最多 8192 字节。", "input");

            // 校验范围仅为原始输入；先附加校验值，再附加用户选择的换行。
            if (checksumLength != 0)
            {
                uint checksum = Calculate(bytes, checksumIndex);
                for (int i = 0; i < checksumLength; i++)
                {
                    int byteIndex = lowByteFirst ? i : checksumLength - 1 - i;
                    bytes.Add((byte)(checksum >> (byteIndex * 8)));
                }
            }
            if (newlineIndex == 1 || newlineIndex == 3) bytes.Add(13);
            if (newlineIndex == 2 || newlineIndex == 3) bytes.Add(10);
            return bytes.ToArray();
        }

        private static List<byte> ParseAscii(string input)
        {
            if (input.Length > MaximumLength) throw new ArgumentException("输入、校验及换行合计最多 8192 字节。", "input");
            List<byte> result = new List<byte>(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] > 127) throw new ArgumentException("ASCII 仅允许 0～127；第 " + (i + 1) + " 个字符超出范围。", "input");
                result.Add((byte)input[i]);
            }
            return result;
        }

        private static List<byte> ParseHex(string input)
        {
            List<byte> result = new List<byte>(Math.Min(input.Length / 2, MaximumLength));
            int cursor = 0;
            while (cursor < input.Length)
            {
                if (IsSeparator(input[cursor])) { cursor++; continue; }
                int start = cursor;
                while (cursor < input.Length && !IsSeparator(input[cursor])) cursor++;
                int end = cursor;
                if (end - start >= 2 && input[start] == '0' && (input[start + 1] == 'x' || input[start + 1] == 'X')) start += 2;
                if (end == start || ((end - start) & 1) != 0)
                    throw new ArgumentException("HEX 每个片段需要偶数个十六进制字符，0x 后不可为空。", "input");
                for (int i = start; i < end; i += 2)
                {
                    int high = HexDigit(input[i]), low = HexDigit(input[i + 1]);
                    if (high < 0 || low < 0) throw new ArgumentException("HEX 只允许 0～9、A～F、0x 前缀及空白/逗号分隔。", "input");
                    if (result.Count == MaximumLength) throw new ArgumentException("输入、校验及换行合计最多 8192 字节。", "input");
                    result.Add((byte)((high << 4) | low));
                }
            }
            return result;
        }

        private static bool IsSeparator(char value) { return char.IsWhiteSpace(value) || value == ','; }
        private static int HexDigit(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        private static uint Calculate(List<byte> bytes, int kind)
        {
            // 固定check向量见CRC RevEng 1-15.htm和16.htm；多项式使用对应反射形式。
            if (kind == 8) return SmallCrc(bytes, 8, 0x07, 0, 0, false);
            if (kind == 9) return SmallCrc(bytes, 8, 0x8C, 0, 0, true);
            if (kind == 10) return SmallCrc(bytes, 16, 0xA001, 0, 0, true);
            if (kind == 11) return SmallCrc(bytes, 16, 0xA001, 0xFFFF, 0xFFFF, true);
            if (kind == 12) return SmallCrc(bytes, 16, 0xA6BC, 0, 0xFFFF, true);
            if (kind >= 5)
            {
                uint sum = 0;
                foreach (byte value in bytes) sum = kind == 7 ? sum ^ value : sum + value;
                return kind == 5 ? unchecked(0u - sum) & 255u : sum & (kind == 13 ? 65535u : 255u);
            }
            // RevEng 参数：Modbus 反射 0x8005/init FFFF；CCITT-FALSE 与 XMODEM
            // 均为非反射 0x1021，init 分别为 FFFF/0000；CRC32 为 ISO-HDLC。
            uint crc = kind == 3 ? 0u : kind == 4 ? 0xFFFFFFFFu : 0xFFFFu;
            foreach (byte value in bytes)
            {
                if (kind == 1 || kind == 4)
                {
                    crc ^= value;
                    uint polynomial = kind == 1 ? 0xA001u : 0xEDB88320u;
                    for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
                }
                else
                {
                    crc ^= (uint)value << 8;
                    for (int bit = 0; bit < 8; bit++) crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021u : crc << 1) & 0xFFFFu;
                }
            }
            return kind == 4 ? crc ^ 0xFFFFFFFFu : crc;
        }
        public static int ChecksumLength(int kind)
        {
            if (kind < 0 || kind >= ChecksumNames.Length) throw new ArgumentOutOfRangeException("kind");
            return kind == 0 ? 0 : kind == 4 ? 4 : (kind >= 1 && kind <= 3) || kind >= 10 ? 2 : 1;
        }
        private static uint SmallCrc(List<byte> bytes, int width, uint polynomial, uint initial, uint xorOut, bool reflected)
        {
            uint crc = initial, mask = (1u << width) - 1, top = 1u << (width - 1);
            foreach (byte value in bytes)
            {
                crc ^= reflected ? value : (uint)value << (width - 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = reflected ? ((crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1) :
                        (((crc & top) != 0 ? (crc << 1) ^ polynomial : crc << 1) & mask);
            }
            return (crc ^ xorOut) & mask;
        }
    }
}
