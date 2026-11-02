using System;
using System.Collections.Generic;

namespace ModbusSimulator
{
    /// <summary>
    /// RTU over TCP 的有界请求流解析器：已知功能码按请求长度/字节计数分帧并校验 CRC。
    /// 未知功能码没有可推导长度，以首个合法 CRC 前缀为边界。坏帧后扫描合法 CRC 重新同步。
    /// TCP 自身没有 RTU 静默间隔，未知功能码的边界只能作此兼容推断。
    /// </summary>
    public sealed class RtuStreamParser
    {
        private readonly List<byte> buffer = new List<byte>(256);
        private bool seeking;
        /// <summary>逐字节输入；完整且 CRC 正确时返回一帧，其余返回 null。</summary>
        public byte[] Feed(byte value)
        {
            buffer.Add(value);
            if (!seeking)
            {
                int expected = RequestLength(0);
                if (expected == 0) return null;
                if (expected > 0)
                {
                    if (buffer.Count < expected) return null;
                    if (ValidCrc(0, expected)) return Take(0, expected);
                    seeking = true;
                }
                else if (expected == -1)
                {
                    int length = UnknownLength(0);
                    if (length != 0) return Take(0, length);
                    if (buffer.Count < 256) return null;
                    seeking = true;
                }
                else seeking = true;
            }
            for (int offset = 0; offset <= buffer.Count - 4; offset++)
            {
                if (buffer[offset] > 247) continue;
                int expected = RequestLength(offset);
                if (expected == -1) expected = UnknownLength(offset);
                if (expected >= 4 && buffer.Count - offset >= expected && ValidCrc(offset, expected)) return Take(offset, expected);
            }
            // 最长合法 RTU ADU 为 256 字节；只需保留末尾可能尚未完成的一帧。
            if (buffer.Count > 256) buffer.RemoveRange(0, buffer.Count - 256);
            return null;
        }
        // 0=待补头；-1=未知功能；-2=不可能的长度/地址；正数=整帧长度。
        private int RequestLength(int offset)
        {
            int available = buffer.Count - offset;
            if (available < 2) return 0;
            if (buffer[offset] > 247) return -2;
            int fc = buffer[offset + 1];
            if (fc >= 1 && fc <= 6) return 8;
            if (fc == 15 || fc == 16)
            {
                if (available < 7) return 0;
                int length = 9 + buffer[offset + 6];
                return length <= 256 ? length : -2;
            }
            return -1;
        }
        private static ushort AddCrc(ushort crc, byte value)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ 0xA001 : crc >> 1);
            return crc;
        }
        private bool ValidCrc(int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length - 2; i++) crc = AddCrc(crc, buffer[offset + i]);
            return buffer[offset + length - 2] == (byte)crc && buffer[offset + length - 1] == (byte)(crc >> 8);
        }
        private int UnknownLength(int offset)
        {
            int available = Math.Min(256, buffer.Count - offset);
            ushort crc = 0xFFFF;
            for (int i = 0; i < available - 2; i++)
            {
                crc = AddCrc(crc, buffer[offset + i]);
                if (i >= 1 && buffer[offset + i + 1] == (byte)crc && buffer[offset + i + 2] == (byte)(crc >> 8)) return i + 3;
            }
            return 0;
        }
        private byte[] Take(int offset, int length)
        {
            byte[] frame = buffer.GetRange(offset, length).ToArray();
            buffer.RemoveRange(0, offset + length); seeking = false; return frame;
        }
    }
}
