using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ModbusSimulator;

internal static class NetworkChecks
{
    private static int passed, failed;
    private static byte[] Hex(string text) { return Array.ConvertAll(text.Split(' '), s => Convert.ToByte(s, 16)); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Same(byte[] a, byte[] b) { Check(a != null && b != null && BitConverter.ToString(a) == BitConverter.ToString(b), "frame mismatch: " + (a == null ? "null" : BitConverter.ToString(a))); }
    private static byte[] Rtu(string text)
    {
        byte[] data = Hex(text), result = new byte[data.Length + 2]; Buffer.BlockCopy(data, 0, result, 0, data.Length);
        ushort crc = RegisterBank.Crc(data, data.Length); result[data.Length] = (byte)crc; result[data.Length + 1] = (byte)(crc >> 8); return result;
    }
    private static void Test(string name, Action action)
    { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }
    private static void Start(SimulatorServer server, string name, params object[] args)
    { MethodInfo method = typeof(SimulatorServer).GetMethod(name); Check(method != null, name + " missing"); method.Invoke(server, args); }
    private static T Field<T>(object source, string name)
    { return (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source); }
    private static byte[] Exact(Stream stream, int count)
    { byte[] data = new byte[count]; int offset = 0; while (offset < count) { int n = stream.Read(data, offset, count - offset); Check(n > 0, "EOF"); offset += n; } return data; }
    private sealed class Parser
    {
        private readonly object parser; private readonly MethodInfo feed;
        public Parser() { Type type = typeof(SimulatorServer).Assembly.GetType("ModbusSimulator.RtuStreamParser"); Check(type != null, "RtuStreamParser missing"); parser = Activator.CreateInstance(type); feed = type.GetMethod("Feed", new Type[] { typeof(byte) }); }
        public List<byte[]> Send(byte[] data)
        { List<byte[]> result = new List<byte[]>(); foreach (byte value in data) { byte[] frame = (byte[])feed.Invoke(parser, new object[] { value }); if (frame != null) result.Add(frame); } return result; }
    }
    private static byte[] Join(byte[] first, byte[] second)
    { byte[] result = new byte[first.Length + second.Length]; Buffer.BlockCopy(first, 0, result, 0, first.Length); Buffer.BlockCopy(second, 0, result, first.Length, second.Length); return result; }
    private static void NoDatagram(UdpClient client)
    {
        try { IPEndPoint from = null; client.Receive(ref from); throw new Exception("unexpected UDP reply"); }
        catch (SocketException ex) { Check(ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock, "UDP receive failure"); }
    }
    public static int Main()
    {
        for (int fc = 1; fc <= 6; fc++) { int function = fc; Test("RTU parser fixed FC" + fc, delegate { Parser p = new Parser(); byte[] frame = Rtu("01 " + function.ToString("X2") + " 00 00 00 01"); List<byte[]> result = p.Send(frame); Check(result.Count == 1, "fixed length frame missing"); Same(result[0], frame); }); }
        Test("RTU parser FC0F variable count", delegate { Parser p = new Parser(); byte[] frame = Rtu("01 0F 00 00 00 09 02 FF 01"); Check(p.Send(frame).Count == 1, "FC0F missing"); });
        Test("RTU parser FC10 variable count", delegate { Parser p = new Parser(); byte[] frame = Rtu("01 10 00 00 00 02 04 12 34 56 78"); Check(p.Send(frame).Count == 1, "FC10 missing"); });
        Test("RTU parser split request", delegate { Parser p = new Parser(); Check(p.Send(Hex("01 03 00")).Count == 0, "partial emitted"); Check(p.Send(Hex("00 00 0A C5 CD")).Count == 1, "split lost"); });
        Test("RTU parser joined requests", delegate { Parser p = new Parser(); byte[] frame = Rtu("01 03 00 00 00 01"); Check(p.Send(Join(frame, frame)).Count == 2, "joined lost"); });
        Test("RTU parser unknown function with CRC", delegate { Parser p = new Parser(); byte[] frame = Rtu("01 17 00 00 00 01"); List<byte[]> result = p.Send(frame); Check(result.Count == 1, "unknown lost"); Same(frame, result[0]); });
        Test("RTU parser CRC rejects and recovers", delegate { Parser p = new Parser(); byte[] bad = Hex("01 03 00 00 00 0A 00 00"), good = Rtu("01 03 00 00 00 01"); Check(p.Send(bad).Count == 0, "CRC bad accepted"); List<byte[]> result = p.Send(good); Check(result.Count == 1, "CRC recovery missing"); Same(good, result[0]); });
        Test("RTU parser oversized byte count recovers", delegate { Parser p = new Parser(); byte[] good = Rtu("01 03 00 00 00 01"); Check(p.Send(Join(Hex("01 10 00 00 00 7F FF"), good)).Count == 1, "oversize recovery missing"); });
        Test("RTU parser preserves payload containing frame-like bytes", delegate { Parser p = new Parser(); byte[] inner = Rtu("01 03 00 00 00 01"); byte[] body = Join(Hex("01 10 00 00 00 04 08"), inner); ushort crc = RegisterBank.Crc(body, body.Length); byte[] outer = Join(body, new byte[] { (byte)crc, (byte)(crc >> 8) }); List<byte[]> result = p.Send(outer); Check(result.Count == 1, "payload framed incorrectly"); Same(outer, result[0]); });
        Test("RTU parser maximum FC10 request", delegate {
            Parser p = new Parser(); byte[] body = new byte[253]; body[0] = 1; body[1] = 16; body[5] = 123; body[6] = 246;
            ushort crc = RegisterBank.Crc(body, body.Length); byte[] frame = Join(body, new byte[] { (byte)crc, (byte)(crc >> 8) });
            List<byte[]> result = p.Send(frame); Check(result.Count == 1, "maximum frame missing"); Same(frame, result[0]);
        });
        Test("RTU parser bounded noise followed by valid request", delegate { Parser p = new Parser(); p.Send(new byte[600]); byte[] good = Rtu("01 03 00 00 00 01"); List<byte[]> result = p.Send(good); Check(result.Count == 1, "noise recovery missing"); Same(good, result[0]); });
        foreach (bool rtu in new bool[] { false, true })
        {
            bool raw = rtu;
            Test((raw ? "RTU UDP" : "MBAP UDP") + " request, peer log and dispose", delegate {
                List<string> logs = new List<string>(); SimulatorServer s = new SimulatorServer(new RegisterBank(), 1, delegate(string x) { lock (logs) logs.Add(x); });
                try {
                    Start(s, "StartUdp", IPAddress.Loopback, 0, raw); Check(s.IsRunning, "not running");
                    IPEndPoint endpoint = (IPEndPoint)Field<UdpClient>(s, "udp").Client.LocalEndPoint;
                    using (UdpClient client = new UdpClient()) {
                        client.Client.ReceiveTimeout = 2000; byte[] request = raw ? Rtu("01 03 00 00 00 01") : Hex("00 13 00 00 00 06 01 03 00 00 00 01");
                        client.Send(request, request.Length, endpoint); IPEndPoint from = null; byte[] response = client.Receive(ref from);
                        Same(response, raw ? Rtu("01 03 02 00 00") : Hex("00 13 00 00 00 05 01 03 02 00 00"));
                        Check(from.Port == endpoint.Port && s.Requests == 1, "UDP endpoint/count mismatch");
                        // 接收线程先将数据报送入 socket；日志可能晚几个调度周期，关闭会等待该线程。
                        s.Dispose(); Check(!s.IsRunning, "dispose running");
                        lock (logs) Check(logs.Exists(delegate(string x) { return x.StartsWith("RX: UDP/") && x.Contains("127.0.0.1:"); }) && logs.Exists(delegate(string x) { return x.StartsWith("TX: UDP/") && x.Contains("127.0.0.1:"); }), "peer/encapsulation log missing");
                        using (UdpClient rebound = new UdpClient(endpoint)) Check(rebound.Client.IsBound, "UDP not released");
                    }
                } finally { s.Dispose(); }
            });
            Test((raw ? "RTU UDP" : "MBAP UDP") + " datagram boundaries, mismatch, broadcast", delegate {
                using (SimulatorServer s = new SimulatorServer(new RegisterBank(), 1, null)) {
                    Start(s, "StartUdp", IPAddress.Loopback, 0, raw); IPEndPoint endpoint = (IPEndPoint)Field<UdpClient>(s, "udp").Client.LocalEndPoint;
                    using (UdpClient client = new UdpClient()) {
                        client.Client.ReceiveTimeout = 150;
                        byte[] request = raw ? Rtu("01 03 00 00 00 01") : Hex("00 01 00 00 00 06 01 03 00 00 00 01");
                        byte[] partial = new byte[request.Length - 1]; Buffer.BlockCopy(request, 0, partial, 0, partial.Length);
                        client.Send(partial, partial.Length, endpoint); NoDatagram(client); client.Send(new byte[] { request[request.Length - 1] }, 1, endpoint); NoDatagram(client);
                        byte[] joined = Join(request, request); client.Send(joined, joined.Length, endpoint); NoDatagram(client);
                        byte[] mismatch = raw ? Rtu("02 06 00 00 12 34") : Hex("00 02 00 00 00 06 02 06 00 00 12 34"); client.Send(mismatch, mismatch.Length, endpoint); NoDatagram(client);
                        byte[] invalid = (byte[])request.Clone(); if (raw) invalid[invalid.Length - 1] ^= 1; else invalid[3] = 1;
                        client.Send(invalid, invalid.Length, endpoint); NoDatagram(client);
                        byte[] broadcastRead = raw ? Rtu("00 03 00 00 00 01") : Hex("00 04 00 00 00 06 00 03 00 00 00 01"); client.Send(broadcastRead, broadcastRead.Length, endpoint); NoDatagram(client);
                        byte[] broadcast = raw ? Rtu("00 06 00 00 12 34") : Hex("00 03 00 00 00 06 00 06 00 00 12 34"); client.Send(broadcast, broadcast.Length, endpoint); NoDatagram(client);
                        Check(s.Bank.Get(2, 0) == 0x1234 && s.Requests == 1, "broadcast write missing or invalid counted");
                        client.Send(request, request.Length, endpoint); IPEndPoint from = null; Same(client.Receive(ref from), raw ? Rtu("01 03 02 12 34") : Hex("00 01 00 00 00 05 01 03 02 12 34"));
                    }
                }
            });
        }
        Test("RTU TCP split joined persistent stream and unknown function", delegate {
            List<string> logs = new List<string>();
            using (SimulatorServer s = new SimulatorServer(new RegisterBank(), 1, delegate(string x) { lock (logs) logs.Add(x); })) {
                Start(s, "StartRtuTcp", IPAddress.Loopback, 0); IPEndPoint endpoint = (IPEndPoint)Field<TcpListener>(s, "listener").LocalEndpoint;
                using (TcpClient client = new TcpClient()) { client.ReceiveTimeout = 2000; client.Connect(endpoint); NetworkStream stream = client.GetStream();
                    byte[] request = Rtu("01 06 00 00 12 34"); stream.Write(request, 0, 3); stream.Write(request, 3, request.Length - 3); Same(Exact(stream, 8), request);
                    byte[] read = Rtu("01 03 00 00 00 01"), joined = Join(read, read); stream.Write(joined, 0, joined.Length); byte[] response = Rtu("01 03 02 12 34"); Same(Exact(stream, 7), response); Same(Exact(stream, 7), response);
                    byte[] unknown = Rtu("01 17 00 00 00 01"); stream.Write(unknown, 0, unknown.Length); Same(Exact(stream, 5), Rtu("01 97 01"));
                    byte[] bad = Hex("01 03 00 00 00 01 00 00"); joined = Join(bad, read); stream.Write(joined, 0, joined.Length); Same(Exact(stream, 7), response);
                    s.Dispose(); Check(!s.IsRunning, "TCP dispose running"); Check(stream.ReadByte() == -1, "TCP client still open");
                    lock (logs) Check(logs.Exists(delegate(string x) { return x.StartsWith("RX: TCP/RTU [127.0.0.1:"); }) && logs.Exists(delegate(string x) { return x.StartsWith("TX: TCP/RTU [127.0.0.1:"); }), "TCP RTU peer/encapsulation log missing");
                }
                using (TcpListenerRelease release = new TcpListenerRelease(endpoint)) { }
            }
        });
        Console.WriteLine("TOTAL " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
    }
    private sealed class TcpListenerRelease : IDisposable
    { private readonly TcpListener listener; public TcpListenerRelease(IPEndPoint endpoint) { listener = new TcpListener(endpoint); listener.Start(); } public void Dispose() { listener.Stop(); } }
}
