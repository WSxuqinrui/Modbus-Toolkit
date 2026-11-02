using System;

namespace ModbusSimulator
{
    /// <summary>标准 Modbus ASCII 封装：冒号、ASCII HEX、LRC、CRLF；不依赖串口设备。</summary>
    public static class AsciiProtocol
    {
        // 最大 PDU 为 253 字节。地址/PDU/LRC 共 255 字节，每字节编码为两个字符。
        public const int MaximumFrameLength = 513;
        internal static int HexValue(byte value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }
        /// <summary>校验完整帧并解码；LRC 是地址与 PDU 字节之和的二进制补码。</summary>
        public static bool TryDecode(byte[] frame, out byte address, out byte[] pdu)
        {
            address = 0; pdu = null;
            if (frame == null || frame.Length < 9 || frame.Length > MaximumFrameLength ||
                frame[0] != ':' || frame[frame.Length - 2] != '\r' || frame[frame.Length - 1] != '\n' ||
                ((frame.Length - 3) & 1) != 0) return false;
            byte[] binary = new byte[(frame.Length - 3) / 2];
            int sum = 0;
            for (int i = 0; i < binary.Length; i++)
            {
                int high = HexValue(frame[1 + 2 * i]), low = HexValue(frame[2 + 2 * i]);
                if (high < 0 || low < 0) return false;
                binary[i] = (byte)((high << 4) | low);
                sum += binary[i];
            }
            if ((sum & 255) != 0) return false;
            address = binary[0];
            pdu = new byte[binary.Length - 2];
            Buffer.BlockCopy(binary, 1, pdu, 0, pdu.Length);
            return true;
        }
        /// <summary>生成大写 HEX ASCII 帧；LRC 不包含冒号、CRLF 或字符本身。</summary>
        public static byte[] Encode(byte address, byte[] pdu)
        {
            if (pdu == null) throw new ArgumentNullException("pdu");
            if (pdu.Length < 1 || pdu.Length > 253) throw new ArgumentOutOfRangeException("pdu");
            byte[] frame = new byte[3 + (pdu.Length + 2) * 2];
            frame[0] = (byte)':';
            int sum = address;
            PutHex(frame, 1, address);
            for (int i = 0; i < pdu.Length; i++) { PutHex(frame, 3 + i * 2, pdu[i]); sum += pdu[i]; }
            PutHex(frame, frame.Length - 4, unchecked((byte)(-sum)));
            frame[frame.Length - 2] = (byte)'\r'; frame[frame.Length - 1] = (byte)'\n';
            return frame;
        }
        private static void PutHex(byte[] destination, int offset, byte value)
        {
            const string digits = "0123456789ABCDEF";
            destination[offset] = (byte)digits[value >> 4]; destination[offset + 1] = (byte)digits[value & 15];
        }
    }

    /// <summary>
    /// 有界流式接收器。调用者提供单调毫秒时间；超过 1 秒的字符间隔丢弃残帧。
    /// 冒号随时开始新帧，非法字符/超长帧丢弃至下一冒号。LRC 由 TryDecode 校验。
    /// </summary>
    public sealed class AsciiFrameParser
    {
        private readonly byte[] buffer = new byte[AsciiProtocol.MaximumFrameLength];
        private int count;
        private long lastCharacter;
        /// <summary>输入一个字符；只有完整、长度合法的 HEX+CRLF 帧才返回非空数组。</summary>
        public byte[] Feed(byte value, long timestampMilliseconds)
        {
            Expire(timestampMilliseconds);
            if (value == ':') { count = 1; buffer[0] = value; lastCharacter = timestampMilliseconds; return null; }
            if (count == 0) return null;
            lastCharacter = timestampMilliseconds;
            if (buffer[count - 1] == '\r')
            {
                if (value != '\n') { count = 0; return null; }
                buffer[count++] = value;
                byte[] complete = new byte[count]; Buffer.BlockCopy(buffer, 0, complete, 0, count);
                count = 0; return complete;
            }
            if (value == '\r')
            {
                // 最少地址、功能、LRC 六个 HEX 字符；CRLF 需要预留两个字节。
                if (count < 7 || ((count - 1) & 1) != 0 || count > buffer.Length - 2) { count = 0; return null; }
                buffer[count++] = value; return null;
            }
            if (AsciiProtocol.HexValue(value) < 0 || count >= buffer.Length - 2) { count = 0; return null; }
            buffer[count++] = value; return null;
        }
        /// <summary>串口空闲时调用，无需等待下一个字符即可清理超时残帧。</summary>
        public void Expire(long timestampMilliseconds)
        {
            if (count != 0 && timestampMilliseconds - lastCharacter > 1000) count = 0;
        }
    }
}
