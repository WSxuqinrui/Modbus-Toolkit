using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ModbusSimulator;

// 针对“写后第一次读到旧值”的诊断；引用发行 EXE，不改动用户数据或现有监听实例。
internal static class ImmediateReadChecks
{
    private static byte[] Bytes(string text) { return Array.ConvertAll(text.Split(' '), s => Convert.ToByte(s, 16)); }
    private static byte[] Exact(Stream stream, int count)
    {
        byte[] result = new byte[count]; int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(result, offset, count - offset);
            if (read == 0) throw new Exception("unexpected EOF");
            offset += read;
        }
        return result;
    }
    private static byte[] Receive(NetworkStream stream)
    {
        byte[] header = Exact(stream, 6);
        byte[] body = Exact(stream, RegisterBank.Word(header, 4));
        byte[] frame = new byte[6 + body.Length];
        Buffer.BlockCopy(header, 0, frame, 0, 6); Buffer.BlockCopy(body, 0, frame, 6, body.Length);
        return frame;
    }
    private static void Same(byte[] got, byte[] wanted, string label)
    {
        if (BitConverter.ToString(got) != BitConverter.ToString(wanted))
            throw new Exception(label + " expected=" + BitConverter.ToString(wanted) + " got=" + BitConverter.ToString(got));
    }
    private static byte[] WriteFrame(bool multiple, int value)
    {
        byte[] frame = multiple ? Bytes("00 02 00 00 00 0B 01 10 00 00 00 02 04 00 00 00 00") :
            Bytes("00 01 00 00 00 06 01 06 00 00 00 00");
        int position = multiple ? 13 : 10;
        frame[position] = (byte)(value >> 8); frame[position + 1] = (byte)value;
        if (multiple) { frame[15] = (byte)((value + 1) >> 8); frame[16] = (byte)(value + 1); }
        return frame;
    }
    private static void ReadValue(byte[] frame, int first, int second)
    {
        byte[] expected = Bytes("00 03 00 00 00 07 01 03 04 00 00 00 00");
        expected[9] = (byte)(first >> 8); expected[10] = (byte)first;
        expected[11] = (byte)(second >> 8); expected[12] = (byte)second;
        Same(frame, expected, "immediate FC03 readback");
    }
    public static int Main()
    {
        try
        {
            byte[] query = Bytes("00 03 00 00 00 06 01 03 00 00 00 02");
            RegisterBank bank = new RegisterBank();
            using (SimulatorServer server = new SimulatorServer(bank, 1, null))
            {
                server.StartTcp(IPAddress.Loopback, 0);
                TcpListener listener = (TcpListener)typeof(SimulatorServer).GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
                using (TcpClient client = new TcpClient())
                {
                    client.NoDelay = true; client.ReceiveTimeout = 2000;
                    client.Connect((IPEndPoint)listener.LocalEndpoint);
                    NetworkStream stream = client.GetStream();
                    for (int kind = 0; kind < 2; kind++)
                    {
                        bank.Set(2, 1, 0);
                        for (int i = 1; i <= 500; i++)
                        {
                            byte[] write = WriteFrame(kind == 1, i);
                            stream.Write(write, 0, write.Length);
                            byte[] ack = Receive(stream);
                            Same(ack, kind == 1 ? Bytes("00 02 00 00 00 06 01 10 00 00 00 02") : write, "write acknowledgement");
                            // 不等待 UI 刷新，也不延时或重发；第一次读必须就是新值。
                            stream.Write(query, 0, query.Length);
                            ReadValue(Receive(stream), i, kind == 1 ? i + 1 : 0);
                        }
                        Console.WriteLine("PASS 500 TCP immediate reads after " + (kind == 0 ? "FC06" : "FC10") + " acknowledgements");
                    }
                    for (int i = 1; i <= 500; i++)
                    {
                        byte[] write = WriteFrame(true, i + 1000);
                        byte[] combined = new byte[write.Length + query.Length];
                        Buffer.BlockCopy(write, 0, combined, 0, write.Length);
                        Buffer.BlockCopy(query, 0, combined, write.Length, query.Length);
                        stream.Write(combined, 0, combined.Length);
                        Same(Receive(stream), Bytes("00 02 00 00 00 06 01 10 00 00 00 02"), "pipelined write ack");
                        ReadValue(Receive(stream), i + 1000, i + 1001);
                    }
                    Console.WriteLine("PASS 500 TCP write/read requests in the same send");
                }
                for (int i = 1; i <= 500; i++)
                {
                    server.ProcessMbap(WriteFrame(true, i));
                    ReadValue(server.ProcessMbap(query), i, i + 1);
                }
                Console.WriteLine("PASS 500 direct MBAP write/read pairs (serial framing processor)");
            }
            Console.WriteLine("TOTAL 2000 immediate-read cycles; stale reads 0; no retries or wait delays");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
