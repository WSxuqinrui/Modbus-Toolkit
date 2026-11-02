using System;
using System.IO;

namespace ModbusMaster
{
    /// <summary>设备返回的 Modbus 异常；这是有效响应，不表示链路断开。</summary>
    public sealed class ModbusRemoteException : IOException
    {
        public byte ExceptionCode { get; private set; }
        public ModbusRemoteException(byte code)
            : base("设备返回 Modbus 异常，异常码 0x" + code.ToString("X2") + "。") { ExceptionCode = code; }
    }

    /// <summary>主机 PDU 编解码。地址使用从 0 开始的协议地址，线圈值使用逻辑 0/1。</summary>
    public static class MasterProtocol
    {
        public static byte[] ReadRequest(int area, int address, int count)
        {
            CheckArea(area, false);
            CheckRange(address, count, area < 2 ? 2000 : 125);
            return Header((byte)(area == 0 ? 1 : area == 1 ? 2 : area == 2 ? 3 : 4), address, count);
        }

        public static byte[] WriteSingle(int area, int address, ushort value)
        {
            CheckArea(area, true); CheckRange(address, 1, 1);
            if (area == 0 && value > 1) throw new ArgumentOutOfRangeException("value", "线圈值必须是 0 或 1。");
            return Header((byte)(area == 0 ? 5 : 6), address, area == 0 ? (value == 0 ? 0 : 0xFF00) : value);
        }

        public static byte[] WriteMultiple(int area, int address, ushort[] values)
        {
            CheckArea(area, true);
            if (values == null) throw new ArgumentNullException("values");
            CheckRange(address, values.Length, area == 0 ? 1968 : 123);
            int bytes = area == 0 ? (values.Length + 7) / 8 : values.Length * 2;
            byte[] pdu = new byte[6 + bytes];
            byte[] header = Header((byte)(area == 0 ? 15 : 16), address, values.Length);
            Buffer.BlockCopy(header, 0, pdu, 0, 5); pdu[5] = (byte)bytes;
            for (int i = 0; i < values.Length; i++)
            {
                if (area == 0)
                {
                    if (values[i] > 1) throw new ArgumentOutOfRangeException("values", "线圈值必须是 0 或 1。");
                    if (values[i] != 0) pdu[6 + i / 8] |= (byte)(1 << (i % 8));
                }
                else { pdu[6 + 2 * i] = (byte)(values[i] >> 8); pdu[7 + 2 * i] = (byte)values[i]; }
            }
            return pdu;
        }

        public static ushort[] ParseRead(byte[] request, byte[] response)
        {
            if (request == null || request.Length != 5 || request[0] < 1 || request[0] > 4)
                throw new ArgumentException("读请求 PDU 无效。", "request");
            int count = Word(request, 3);
            CheckRange(Word(request, 1), count, request[0] <= 2 ? 2000 : 125);
            CheckResponse(request, response);
            int bytes = request[0] <= 2 ? (count + 7) / 8 : count * 2;
            if (response.Length != bytes + 2 || response[1] != bytes) throw new IOException("读响应字节数与请求数量不一致。");
            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++) values[i] = request[0] <= 2 ? (ushort)((response[2 + i / 8] >> (i % 8)) & 1) : (ushort)Word(response, 2 + i * 2);
            return values;
        }

        public static void ValidateWrite(byte[] request, byte[] response)
        {
            ValidateWriteRequest(request);
            CheckResponse(request, response);
            if (response.Length != 5) throw new IOException("写响应长度应为 5 字节 PDU。");
            for (int i = 0; i < 5; i++) if (response[i] != request[i]) throw new IOException("写响应回显的地址、数量或数值与请求不一致。");
        }

        private static void ValidateWriteRequest(byte[] pdu)
        {
            if (pdu == null || pdu.Length < 5) throw new ArgumentException("写请求 PDU 无效。", "request");
            byte fc = pdu[0];
            if (fc == 5 || fc == 6)
            {
                if (pdu.Length != 5 || (fc == 5 && Word(pdu, 3) != 0 && Word(pdu, 3) != 0xFF00))
                    throw new ArgumentException("单点写请求 PDU 无效。", "request");
                return;
            }
            if ((fc != 15 && fc != 16) || pdu.Length < 6) throw new ArgumentException("写功能码无效。", "request");
            int count = Word(pdu, 3); CheckRange(Word(pdu, 1), count, fc == 15 ? 1968 : 123);
            int bytes = fc == 15 ? (count + 7) / 8 : count * 2;
            if (pdu[5] != bytes || pdu.Length != bytes + 6) throw new ArgumentException("批量写请求字节数无效。", "request");
        }

        private static void CheckResponse(byte[] request, byte[] response)
        {
            if (response == null || response.Length < 1) throw new IOException("设备响应 PDU 为空。");
            if (response[0] == (byte)(request[0] | 0x80))
            {
                if (response.Length != 2) throw new IOException("设备异常响应长度无效。");
                throw new ModbusRemoteException(response[1]);
            }
            if (response[0] != request[0]) throw new IOException("设备响应功能码与请求不一致。");
        }
        private static byte[] Header(byte fc, int address, int value)
        { return new byte[] { fc, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value }; }
        private static int Word(byte[] data, int offset) { return (data[offset] << 8) | data[offset + 1]; }
        private static void CheckArea(int area, bool write)
        {
            if (area < 0 || area > 3 || (write && area != 0 && area != 2)) throw new ArgumentOutOfRangeException("area", "此数据区不支持该操作。");
        }
        private static void CheckRange(int address, int count, int maximum)
        {
            if (address < 0 || address > 65535) throw new ArgumentOutOfRangeException("address");
            if (count < 1 || count > maximum || address + count > 65536) throw new ArgumentOutOfRangeException("count");
        }
    }
}
