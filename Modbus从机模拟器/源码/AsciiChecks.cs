using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using ModbusSimulator;

// 离线协议/流边界回归；通过反射允许同一测试先对缺失 ASCII 功能的旧版执行 RED。
internal static class AsciiChecks
{
    private static int passed, failed;
    private static readonly byte[] Read = Bytes(":010300000001FB\r\n");
    private static byte[] Bytes(string value) { return Encoding.ASCII.GetBytes(value); }
    private static void Equal(string expected, byte[] actual)
    {
        string value = actual == null ? null : Encoding.ASCII.GetString(actual);
        if (value != expected) throw new Exception("expected " + Escape(expected) + ", actual " + Escape(value));
    }
    private static string Escape(string value) { return value == null ? "<null>" : value.Replace("\r", "\\r").Replace("\n", "\\n"); }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    private static byte[] Process(SimulatorServer server, byte[] frame)
    {
        MethodInfo method = typeof(SimulatorServer).GetMethod("ProcessAscii");
        Check(method != null, "ProcessAscii missing: cannot process a standard ASCII request");
        return (byte[])method.Invoke(server, new object[] { frame });
    }
    private sealed class Parser
    {
        private readonly object instance;
        private readonly MethodInfo feed, expire;
        public Parser()
        {
            Type type = typeof(SimulatorServer).Assembly.GetType("ModbusSimulator.AsciiFrameParser");
            Check(type != null, "AsciiFrameParser missing: cannot receive stream fragments");
            instance = Activator.CreateInstance(type);
            feed = type.GetMethod("Feed", new Type[] { typeof(byte), typeof(long) });
            expire = type.GetMethod("Expire");
            Check(feed != null && expire != null, "stream parser API missing");
        }
        public List<byte[]> Send(byte[] data, long time)
        {
            List<byte[]> result = new List<byte[]>();
            foreach (byte value in data)
            {
                byte[] frame = (byte[])feed.Invoke(instance, new object[] { value, time });
                if (frame != null) result.Add(frame);
            }
            return result;
        }
        public void Expire(long time) { expire.Invoke(instance, new object[] { time }); }
    }
    public static int Main()
    {
        Test("FC03 read known LRC vector", delegate { using (SimulatorServer s = NewServer()) Equal(":0103020000FA\r\n", Process(s, Read)); });
        Test("FC06 echo and committed register", delegate {
            using (SimulatorServer s = NewServer()) { Equal(":010600001234B3\r\n", Process(s, Bytes(":010600001234B3\r\n"))); Check(s.Bank.Get(2, 0) == 0x1234, "write missing"); Check(s.Requests == 1, "request not counted"); }
        });
        Test("FC10 multiple register write", delegate {
            using (SimulatorServer s = NewServer()) { Equal(":011000000002ED\r\n", Process(s, Bytes(":011000000002041234ABCD2B\r\n"))); Check(s.Bank.Get(2, 0) == 0x1234 && s.Bank.Get(2, 1) == 0xABCD, "multiple write missing"); }
        });
        Test("FC05 coil write", delegate { using (SimulatorServer s = NewServer()) { Equal(":01050000FF00FB\r\n", Process(s, Bytes(":01050000FF00FB\r\n"))); Check(s.Bank.Get(0, 0) == 1, "coil missing"); } });
        Test("broadcast write commits without reply", delegate { using (SimulatorServer s = NewServer()) { Equal(null, Process(s, Bytes(":000600001234B4\r\n"))); Check(s.Bank.Get(2, 0) == 0x1234 && s.Requests == 1, "broadcast missing"); } });
        Test("broadcast read ignored", delegate { using (SimulatorServer s = NewServer()) { Equal(null, Process(s, Bytes(":000300000001FC\r\n"))); Check(s.Requests == 0, "broadcast read counted"); } });
        Test("station mismatch ignored", delegate { using (SimulatorServer s = NewServer()) { Equal(null, Process(s, Bytes(":020600001234B2\r\n"))); Check(s.Bank.Get(2, 0) == 0 && s.Requests == 0, "mismatch modified state"); } });
        Test("unsupported function exception", delegate { using (SimulatorServer s = NewServer()) Equal(":01970167\r\n", Process(s, Bytes(":0117E8\r\n"))); });
        Test("bad PDU receives illegal value", delegate { using (SimulatorServer s = NewServer()) Equal(":01830379\r\n", Process(s, Bytes(":010300FC\r\n"))); });
        string[] invalid = { ":010300000001FC\r\n", "010300000001FB\r\n", ":010300000001FB\n", ":010300000001FB\r", ":010300000001FB\r\nX", ":010300000001F\r\n", ":010300000001FG\r\n", ":010300000001fb\r\n", ":\r\n", ":01FF\r\n", ":010300000001FB\rX\n" };
        for (int i = 0; i < invalid.Length; i++) {
            string input = invalid[i]; Test("reject malformed ASCII " + i, delegate { using (SimulatorServer s = NewServer()) { Equal(null, Process(s, Bytes(input))); Check(s.Requests == 0 && s.Bank.Get(2, 0) == 0, "invalid mutated state"); } });
        }
        Test("null ASCII ignored", delegate { using (SimulatorServer s = NewServer()) Equal(null, Process(s, null)); });
        Test("RX log readable plus HEX, processing alone never logs TX", delegate {
            List<string> logs = new List<string>(); using (SimulatorServer s = new SimulatorServer(new RegisterBank(), 1, logs.Add)) Process(s, Read);
            Check(logs.Exists(delegate(string x) { return x.StartsWith("RX: ") && x.Contains(":010300000001FB\\r\\n") && x.Contains("3A 30 31"); }), "ASCII RX log missing");
            Check(!logs.Exists(delegate(string x) { return x.StartsWith("TX"); }), "unsent response logged as TX");
        });
        Test("MBAP RX colon format and no premature TX", delegate {
            List<string> logs = new List<string>(); using (SimulatorServer s = new SimulatorServer(new RegisterBank(), 1, logs.Add)) s.ProcessMbap(new byte[] {0,1,0,0,0,6,1,3,0,0,0,1});
            Check(logs.Count == 1 && logs[0].StartsWith("RX: "), "MBAP log format");
        });
        Test("TCP transmitted response logs TX colon and peer", delegate {
            List<string> logs = new List<string>(); ManualResetEvent transmitted = new ManualResetEvent(false);
            using (SimulatorServer s = new SimulatorServer(new RegisterBank(), 1, delegate(string x) { lock (logs) logs.Add(x); if (x.StartsWith("TX: [")) transmitted.Set(); }))
            {
                s.StartTcp(IPAddress.Loopback, 0);
                TcpListener listener = (TcpListener)typeof(SimulatorServer).GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(s);
                using (TcpClient client = new TcpClient())
                {
                    client.ReceiveTimeout = 2000; client.Connect((IPEndPoint)listener.LocalEndpoint);
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] request = {0,1,0,0,0,6,1,3,0,0,0,1}; stream.Write(request, 0, request.Length);
                        byte[] reply = new byte[11]; int offset = 0;
                        while (offset < reply.Length) { int received = stream.Read(reply, offset, reply.Length - offset); Check(received > 0, "TCP EOF"); offset += received; }
                        Check(BitConverter.ToString(reply) == "00-01-00-00-00-05-01-03-02-00-00", "TCP response changed");
                        Check(transmitted.WaitOne(2000), "TCP successful Write not logged");
                        lock (logs) Check(logs.Exists(delegate(string x) { return x.StartsWith("TX: [127.0.0.1:") && x.Contains("] 00 01"); }), "TCP peer missing");
                    }
                }
            }
            // Dispose 不等待后台连接清理，事件保留至进程退出以避免日志回调与销毁竞争。
        });
        Test("serial ASCII rejects 6 data bits before opening COM", delegate {
            MethodInfo method = typeof(SimulatorServer).GetMethod("StartAscii"); Check(method != null, "StartAscii missing");
            using (SimulatorServer s = NewServer()) { try { method.Invoke(s, new object[] { "NONEXISTENT", 9600, System.IO.Ports.Parity.Even, System.IO.Ports.StopBits.One, 6 }); throw new Exception("invalid bits accepted"); } catch (TargetInvocationException ex) { Check(ex.InnerException is ArgumentOutOfRangeException, "data bits validation missing"); } }
        });
        Test("stream split frame", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":0103000"), 0).Count == 0, "partial emitted"); List<byte[]> frames = p.Send(Bytes("00001FB\r\n"), 30); Check(frames.Count == 1, "split lost"); Equal(":010300000001FB\r\n", frames[0]); });
        Test("stream CRLF split", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":010300000001FB\r"), 0).Count == 0, "CR terminated"); Check(p.Send(Bytes("\n"), 500).Count == 1, "LF lost"); });
        Test("stream concatenated frames", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":010300000001FB\r\n:010300000001FB\r\n"), 0).Count == 2, "joined lost"); });
        Test("stream discards noise", delegate { Parser p = new Parser(); Check(p.Send(Bytes("garbage\r\n:010300000001FB\r\n"), 0).Count == 1, "noise not discarded"); });
        Test("stream invalid hex resets until colon", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":0103Z0000001FB\r\n"), 0).Count == 0, "invalid emitted"); Check(p.Send(Read, 10).Count == 1, "recovery missing"); });
        Test("stream embedded colon restarts frame", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":0103:010300000001FB\r\n"), 0).Count == 1, "colon resync missing"); });
        Test("stream colon after CR restarts frame", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":010300000001FB\r:010300000001FB\r\n"), 0).Count == 1, "CR colon resync missing"); });
        Test("stream bad terminator ignored", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":010300000001FB\n:010300000001FB\rX\n"), 0).Count == 0, "bad termination emitted"); Check(p.Send(Read, 10).Count == 1, "bad terminator recovery"); });
        Test("stream odd hex count ignored", delegate { Parser p = new Parser(); Check(p.Send(Bytes(":010300000001F\r\n"), 0).Count == 0, "odd emitted"); });
        Test("stream one second intercharacter accepted", delegate { Parser p = new Parser(); p.Send(Bytes(":0103"), 0); Check(p.Send(Bytes("00000001FB\r\n"), 1000).Count == 1, "1000ms invalidated"); });
        Test("stream over one second intercharacter discarded", delegate { Parser p = new Parser(); p.Send(Bytes(":0103"), 0); Check(p.Send(Bytes("00000001FB\r\n"), 1001).Count == 0, "expired accepted"); Check(p.Send(Read, 1002).Count == 1, "timeout resync missing"); });
        Test("stream idle expiry discards partial frame", delegate { Parser p = new Parser(); p.Send(Bytes(":0103"), 0); p.Expire(1001); Check(p.Send(Bytes("00000001FB\r\n"), 1002).Count == 0, "idle expiry lost"); });
        // 1 address + 253-byte PDU + LRC = 255 bytes = 510 HEX; colon + CRLF -> 513 characters.
        string maximum = ":0117" + new string('0', 504) + "E8\r\n";
        Test("stream accepts maximum 513 character frame", delegate { Parser p = new Parser(); List<byte[]> frames = p.Send(Bytes(maximum), 0); Check(frames.Count == 1 && frames[0].Length == 513, "maximum lost"); using (SimulatorServer s = NewServer()) Equal(":01970167\r\n", Process(s, frames[0])); });
        Test("oversized 515 character frame rejected and resynchronized", delegate { Parser p = new Parser(); string over = ":0117" + new string('0', 506) + "E8\r\n"; Check(p.Send(Bytes(over), 0).Count == 0, "oversize emitted"); using (SimulatorServer s = NewServer()) Equal(null, Process(s, Bytes(over))); Check(p.Send(Read, 10).Count == 1, "oversize recovery missing"); });
        Test("maximum response for 125 register read", delegate { using (SimulatorServer s = NewServer()) { byte[] reply = Process(s, Bytes(":01030000007D7F\r\n")); Check(reply != null && reply.Length == 511, "max FC03 response length"); Check(Encoding.ASCII.GetString(reply).StartsWith(":0103FA"), "max response header"); } });
        Console.WriteLine("TOTAL " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
    private static SimulatorServer NewServer() { return new SimulatorServer(new RegisterBank(), 1, null); }
}
