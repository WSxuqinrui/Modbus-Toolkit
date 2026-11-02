using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace ModbusMaster
{
    public static class MasterWireChecks
    {
        private static int passed, failed;
        private static void Check(string name, Action test)
        { try { test(); passed++; Console.WriteLine("PASS: " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + name + " " + ex); } }
        private static void Assert(bool value, string text) { if (!value) throw new Exception(text); }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private static byte[] Read(NetworkStream stream, int count)
        { byte[] bytes = new byte[count]; int got = 0; while (got < count) { int n = stream.Read(bytes, got, count - got); if (n == 0) throw new EndOfStreamException(); got += n; } return bytes; }
        private static byte[] ReadMbap(NetworkStream stream)
        { byte[] header = Read(stream, 6), body = Read(stream, (header[4] << 8) | header[5]); byte[] result = new byte[6 + body.Length]; Buffer.BlockCopy(header, 0, result, 0, 6); Buffer.BlockCopy(body, 0, result, 6, body.Length); return result; }
        // 独立手写 MBAP 帧构造，用 peer 收到的 TID 回应，不调用被测编码器计算期望。
        private static byte[] Mbap(byte[] request, byte unit, byte[] pdu, int tidOffset)
        {
            ushort tid = unchecked((ushort)(((request[0] << 8) | request[1]) + tidOffset));
            byte[] result = new byte[7 + pdu.Length]; result[0] = (byte)(tid >> 8); result[1] = (byte)tid; result[4] = (byte)((pdu.Length + 1) >> 8); result[5] = (byte)(pdu.Length + 1); result[6] = unit;
            Buffer.BlockCopy(pdu, 0, result, 7, pdu.Length); return result;
        }
        private static void Send(NetworkStream stream, byte[] frame) { stream.Write(frame, 0, frame.Length); }
        private static void Split(NetworkStream stream, byte[] frame)
        { for (int i = 0; i < frame.Length; i++) { stream.Write(frame, i, 1); Thread.Sleep(2); } }
        private static byte[] Query { get { return new byte[] { 3, 0, 0, 0, 1 }; } }
        private static void Tcp(int mode, Action<NetworkStream> peer, Action<ModbusMasterClient, List<string>> client, int timeout, IPAddress address)
        {
            TcpListener listener = new TcpListener(address, 0); listener.Start();
            Exception peerFailure = null;
            Thread worker = new Thread(delegate()
            {
                try { using (TcpClient accepted = listener.AcceptTcpClient()) { accepted.ReceiveTimeout = 2500; accepted.SendTimeout = 2500; peer(accepted.GetStream()); } }
                catch (Exception ex) { peerFailure = ex; }
            }); worker.IsBackground = true; worker.Start();
            try
            {
                List<string> logs = new List<string>();
                using (ModbusMasterClient connection = new ModbusMasterClient(new MasterConnectionOptions { Mode = mode, Host = address.ToString(), Port = ((IPEndPoint)listener.LocalEndpoint).Port, TimeoutMs = timeout }, delegate(string line) { lock (logs) logs.Add(line); }))
                { connection.Connect(); client(connection, logs); }
                Assert(worker.Join(3500), "peer worker did not end");
                if (peerFailure != null) throw new Exception("peer failed", peerFailure);
            }
            finally { listener.Stop(); }
        }
        private static void Tcp(int mode, Action<NetworkStream> peer, Action<ModbusMasterClient, List<string>> client, int timeout)
        { Tcp(mode, peer, client, timeout, IPAddress.Loopback); }
        private static void Udp(int mode, Action<UdpClient, IPEndPoint, byte[]> peer, Action<ModbusMasterClient> client, int timeout)
        {
            using (UdpClient server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                server.Client.ReceiveTimeout = 2500; Exception failure = null;
                Thread worker = new Thread(delegate() { try { IPEndPoint endpoint = null; byte[] request = server.Receive(ref endpoint); peer(server, endpoint, request); } catch (Exception ex) { failure = ex; } }); worker.IsBackground = true; worker.Start();
                using (ModbusMasterClient connection = new ModbusMasterClient(new MasterConnectionOptions { Mode = mode, Port = ((IPEndPoint)server.Client.LocalEndPoint).Port, TimeoutMs = timeout }, null))
                { connection.Connect(); client(connection); }
                Assert(worker.Join(3500), "UDP peer did not end"); if (failure != null) throw new Exception("UDP peer failed", failure);
            }
        }
        private static void Send(UdpClient udp, IPEndPoint peer, byte[] frame) { udp.Send(frame, frame.Length, peer); }
        private static void Read1234(ModbusMasterClient c)
        { byte[] response = c.Execute(1, Query); ushort[] values = MasterProtocol.ParseRead(Query, response); Assert(values.Length == 1 && values[0] == 0x1234, "wrong matched value"); }
        private static int FreePort(bool udp)
        {
            if (udp) { using (UdpClient socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) return ((IPEndPoint)socket.Client.LocalEndPoint).Port; }
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
        }
        private static void Simulator(int mode, Assembly assembly)
        {
            Type bankType = assembly.GetType("ModbusSimulator.RegisterBank", true), serverType = assembly.GetType("ModbusSimulator.SimulatorServer", true);
            object bank = Activator.CreateInstance(bankType);
            bankType.GetMethod("Set").Invoke(bank, new object[] { 1, 0, (ushort)1 });
            bankType.GetMethod("Set").Invoke(bank, new object[] { 3, 0, (ushort)4321 });
            object server = Activator.CreateInstance(serverType, new object[] { bank, (byte)1, null });
            int port = FreePort(mode == 4 || mode == 6);
            try
            {
                if (mode == 0) serverType.GetMethod("StartTcp").Invoke(server, new object[] { IPAddress.Loopback, port });
                else if (mode == 5) serverType.GetMethod("StartRtuTcp").Invoke(server, new object[] { IPAddress.Loopback, port });
                else serverType.GetMethod("StartUdp").Invoke(server, new object[] { IPAddress.Loopback, port, mode == 6 });
                using (ModbusMasterClient c = new ModbusMasterClient(new MasterConnectionOptions { Mode = mode, Port = port, TimeoutMs = 1500 }, null))
                {
                    c.Connect();
                    for (int cycle = 0; cycle < 20; cycle++)
                    {
                        byte[] write = MasterProtocol.WriteSingle(2, 0, (ushort)(1234 + cycle)); MasterProtocol.ValidateWrite(write, c.Execute(1, write));
                        byte[] read = MasterProtocol.ReadRequest(2, 0, 1); Assert(MasterProtocol.ParseRead(read, c.Execute(1, read))[0] == 1234 + cycle, "FC06 immediate read stale");
                        write = MasterProtocol.WriteMultiple(2, 1, new ushort[] { (ushort)cycle, 0xABCD }); MasterProtocol.ValidateWrite(write, c.Execute(1, write));
                        read = MasterProtocol.ReadRequest(2, 1, 2); ushort[] words = MasterProtocol.ParseRead(read, c.Execute(1, read)); Assert(words[0] == cycle && words[1] == 0xABCD, "FC10 immediate read stale");
                    }
                    byte[] request = MasterProtocol.WriteSingle(0, 0, 1); MasterProtocol.ValidateWrite(request, c.Execute(1, request));
                    request = MasterProtocol.WriteMultiple(0, 1, new ushort[] { 1, 0, 1 }); MasterProtocol.ValidateWrite(request, c.Execute(1, request));
                    request = MasterProtocol.ReadRequest(0, 0, 4); ushort[] bits = MasterProtocol.ParseRead(request, c.Execute(1, request)); Assert(bits[0] == 1 && bits[1] == 1 && bits[2] == 0 && bits[3] == 1, "FC01/05/0F coil readback");
                    request = MasterProtocol.ReadRequest(1, 0, 1); Assert(MasterProtocol.ParseRead(request, c.Execute(1, request))[0] == 1, "FC02 independent read");
                    request = MasterProtocol.ReadRequest(3, 0, 1); Assert(MasterProtocol.ParseRead(request, c.Execute(1, request))[0] == 4321, "FC04 independent read");
                    request = MasterProtocol.ReadRequest(2, 1000, 1); Throws<ModbusRemoteException>(delegate { MasterProtocol.ParseRead(request, c.Execute(1, request)); });
                    Assert(c.IsConnected, "remote exception closed transport");
                    request = MasterProtocol.ReadRequest(2, 0, 1); Assert(MasterProtocol.ParseRead(request, c.Execute(1, request))[0] == 1253, "connection unusable after exception");
                }
            }
            finally { ((IDisposable)server).Dispose(); }
        }
        public static int Main(string[] args)
        {
            Check("TCP MBAP skips wrong TID/Unit/FC/shape then fragmented response", delegate
            {
                Tcp(0, delegate(NetworkStream s) { byte[] q = ReadMbap(s); Send(s, Mbap(q, 1, new byte[] { 3, 2, 0, 9 }, 1)); Send(s, Mbap(q, 2, new byte[] { 3, 2, 0, 8 }, 0)); Send(s, Mbap(q, 1, new byte[] { 4, 2, 0, 7 }, 0)); Send(s, Mbap(q, 1, new byte[] { 3, 4, 0, 6 }, 0)); Split(s, Mbap(q, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0)); },
                    delegate(ModbusMasterClient c, List<string> logs) { Read1234(c); Assert(c.IsConnected, "closed after valid response"); Assert(logs.Exists(delegate(string x) { return x.StartsWith("TX:") && x.Contains("TID=") && x.Contains("127.0.0.1:"); }), "TX peer/TID log"); Assert(logs.Exists(delegate(string x) { return x.StartsWith("RX:") && x.Contains("TID="); }), "RX log"); }, 700);
            });
            Check("TCP next TID and stale prior response", delegate
            {
                Tcp(0, delegate(NetworkStream s) { byte[] first = ReadMbap(s); Send(s, Mbap(first, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0)); byte[] second = ReadMbap(s); Assert(((second[0] << 8) | second[1]) == (((first[0] << 8) | first[1]) + 1), "TID did not increment"); Send(s, Mbap(first, 1, new byte[] { 3, 2, 0, 1 }, 0)); Send(s, Mbap(second, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0)); }, delegate(ModbusMasterClient c, List<string> logs) { Read1234(c); Read1234(c); }, 700);
            });
            Check("device exception PDU keeps connection", delegate
            {
                Tcp(0, delegate(NetworkStream s) { byte[] q = ReadMbap(s); Send(s, Mbap(q, 1, new byte[] { 0x83, 2 }, 0)); q = ReadMbap(s); Send(s, Mbap(q, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0)); }, delegate(ModbusMasterClient c, List<string> logs) { Throws<ModbusRemoteException>(delegate { MasterProtocol.ParseRead(Query, c.Execute(1, Query)); }); Assert(c.IsConnected, "exception disconnected"); Read1234(c); }, 700);
            });
            Check("total deadline ignores repeated wrong TID and disconnects", delegate
            {
                Tcp(0, delegate(NetworkStream s) { byte[] q = ReadMbap(s); try { for (int i = 0; i < 25; i++) { Send(s, Mbap(q, 1, new byte[] { 3, 2, 0, 1 }, 1)); Thread.Sleep(20); } } catch (IOException) { } }, delegate(ModbusMasterClient c, List<string> logs) { Stopwatch watch = Stopwatch.StartNew(); Throws<TimeoutException>(delegate { c.Execute(1, Query); }); Assert(watch.ElapsedMilliseconds < 600, "per-frame timeout reset"); Assert(!c.IsConnected, "timeout did not disconnect"); }, 150);
            });
            Check("TCP truncated MBAP closes transport", delegate { Tcp(0, delegate(NetworkStream s) { byte[] q = ReadMbap(s); byte[] frame = Mbap(q, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0); s.Write(frame, 0, 8); }, delegate(ModbusMasterClient c, List<string> logs) { Throws<EndOfStreamException>(delegate { c.Execute(1, Query); }); Assert(!c.IsConnected, "short frame connected"); }, 500); });
            Check("TCP impossible Length rejected with disconnect", delegate { Tcp(0, delegate(NetworkStream s) { byte[] q = ReadMbap(s); Send(s, new byte[] { q[0], q[1], 0, 0, 1, 0 }); }, delegate(ModbusMasterClient c, List<string> logs) { Throws<IOException>(delegate { c.Execute(1, Query); }); Assert(!c.IsConnected, "invalid length connected"); }, 500); });
            Check("RTU TCP resync garbage/bad CRC/wrong Unit/wrong FC and fragmentation", delegate
            {
                Tcp(5, delegate(NetworkStream s) { Read(s, 8); Send(s, new byte[] { 0xFF, 1, 3, 250, 0, 1, 3, 2, 0, 1, 0x79, 0x85 }); Send(s, ModbusMasterClient.EncodeRtu(2, new byte[] { 3, 2, 0, 8 })); Send(s, ModbusMasterClient.EncodeRtu(1, new byte[] { 4, 2, 0, 9 })); Split(s, new byte[] { 1, 3, 2, 0x12, 0x34, 0xB5, 0x33 }); }, delegate(ModbusMasterClient c, List<string> logs) { Read1234(c); }, 700);
            });
            Check("RTU only bad CRC expires and disconnects", delegate { Tcp(5, delegate(NetworkStream s) { Read(s, 8); Send(s, new byte[] { 1, 3, 2, 0, 1, 0x79, 0x85 }); Thread.Sleep(250); }, delegate(ModbusMasterClient c, List<string> logs) { Throws<TimeoutException>(delegate { c.Execute(1, Query); }); Assert(!c.IsConnected, "CRC timeout connected"); }, 120); });
            Check("Dispose interrupts in-flight TCP wait without transaction lock", delegate
            {
                Tcp(0, delegate(NetworkStream s) { ReadMbap(s); Thread.Sleep(350); }, delegate(ModbusMasterClient c, List<string> logs)
                {
                    Exception failure = null; Thread request = new Thread(delegate() { try { c.Execute(1, Query); } catch (Exception ex) { failure = ex; } }); request.IsBackground = true; request.Start(); Thread.Sleep(50);
                    Stopwatch watch = Stopwatch.StartNew(); c.Dispose(); Assert(watch.ElapsedMilliseconds < 150, "Dispose waited transaction lock"); Assert(request.Join(300), "request did not unblock"); Assert(failure != null && !c.IsConnected, "Dispose failure state");
                }, 2000);
            });
            Check("UDP MBAP rejects short/length/TID/wrong-peer datagrams", delegate
            {
                Udp(4, delegate(UdpClient s, IPEndPoint endpoint, byte[] q) { Send(s, endpoint, new byte[] { 0, 1, 0 }); byte[] invalid = Mbap(q, 1, new byte[] { 3, 2, 0, 9 }, 0); invalid[5]++; Send(s, endpoint, invalid); Send(s, endpoint, Mbap(q, 1, new byte[] { 3, 2, 0, 8 }, 1)); using (UdpClient rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) { Send(rogue, endpoint, Mbap(q, 1, new byte[] { 3, 2, 0, 7 }, 0)); } Send(s, endpoint, Mbap(q, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0)); }, delegate(ModbusMasterClient c) { Read1234(c); }, 700);
            });
            Check("UDP RTU rejects bad CRC/short/wrong FC/wrong Unit", delegate
            {
                Udp(6, delegate(UdpClient s, IPEndPoint endpoint, byte[] q) { Send(s, endpoint, new byte[] { 1, 3, 2 }); Send(s, endpoint, new byte[] { 1, 3, 2, 0, 1, 0x79, 0x85 }); Send(s, endpoint, ModbusMasterClient.EncodeRtu(2, new byte[] { 3, 2, 0, 1 })); Send(s, endpoint, ModbusMasterClient.EncodeRtu(1, new byte[] { 4, 2, 0, 2 })); Send(s, endpoint, new byte[] { 1, 3, 2, 0x12, 0x34, 0xB5, 0x33 }); }, delegate(ModbusMasterClient c) { Read1234(c); }, 700);
            });
            Check("UDP timeout disconnects and never retries write", delegate
            {
                Udp(4, delegate(UdpClient s, IPEndPoint endpoint, byte[] q) { Assert(q[7] == 6, "not write request"); Thread.Sleep(250); Assert(s.Available == 0, "write was automatically retried"); }, delegate(ModbusMasterClient c) { Throws<TimeoutException>(delegate { c.Execute(1, new byte[] { 6, 0, 0, 0, 1 }); }); Assert(!c.IsConnected, "UDP timeout connected"); }, 120);
            });
            Check("RTU parser bounded resync after long garbage", delegate { MasterRtuFrameParser parser = new MasterRtuFrameParser(); for (int i = 0; i < 4000; i++) parser.Feed(0xFF); parser.Feed(1); parser.Feed(3); parser.Feed(250); byte[] result = null; foreach (byte b in new byte[] { 1, 3, 2, 0x12, 0x34, 0xB5, 0x33 }) { byte[] found = parser.Feed(b); if (found != null) result = found; } Assert(result != null && result.Length == 7, "no bounded recovery"); });
            Check("Dispose interrupts serial RTU quiet wait without opening COM", delegate
            {
                using (ModbusMasterClient c = new ModbusMasterClient(new MasterConnectionOptions { Mode = 1, Com = "COM1", Baud = 1200 }, null))
                using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
                {
                    // 复用客户端持有的同一 helper/取消信号，只在进程中验证等待，不打开串口。
                    BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                    MasterRtuSilentInterval timing = (MasterRtuSilentInterval)typeof(ModbusMasterClient).GetField("rtuSilence", fields).GetValue(c);
                    ManualResetEventSlim signal = (ManualResetEventSlim)typeof(ModbusMasterClient).GetField("disposeSignal", fields).GetValue(c);
                    timing.RecordTransmitQueued(Stopwatch.GetTimestamp(), 256); Exception failure = null;
                    Thread worker = new Thread(delegate() { entered.Set(); try { timing.Wait(Stopwatch.GetTimestamp, delegate { return 1000; }, signal.Wait, null); } catch (Exception ex) { failure = ex; } });
                    worker.IsBackground = true; worker.Start(); Assert(entered.Wait(1000), "quiet worker not started");
                    Stopwatch watch = Stopwatch.StartNew(); c.Dispose(); Assert(worker.Join(300), "serial quiet wait not cancelled");
                    Assert(watch.ElapsedMilliseconds < 300 && failure is ObjectDisposedException, "Dispose did not interrupt serial quiet timer");
                }
            });
            Check("ASCII parser recovers short/invalid LRC/fragmented frames", delegate { AsciiFrameParser parser = new AsciiFrameParser(); byte unit; byte[] pdu; bool valid = false; byte[] good = AsciiProtocol.Encode(1, new byte[] { 3, 2, 0x12, 0x34 }); byte[] bad = (byte[])good.Clone(); bad[bad.Length - 3] = bad[bad.Length - 3] == '0' ? (byte)'1' : (byte)'0'; List<byte> input = new List<byte>(new byte[] { (byte)':', (byte)'0', (byte)'1' }); input.AddRange(bad); input.AddRange(good); long time = 0; foreach (byte b in input) { byte[] f = parser.Feed(b, time++); if (f != null && AsciiProtocol.TryDecode(f, out unit, out pdu)) { Assert(!valid, "bad LRC accepted"); valid = unit == 1 && pdu.Length == 4 && pdu[2] == 0x12 && pdu[3] == 0x34; } } Assert(valid, "ASCII did not recover"); });
            if (Socket.OSSupportsIPv6) Check("IPv6 loopback TCP MBAP", delegate { Tcp(0, delegate(NetworkStream s) { byte[] q = ReadMbap(s); Send(s, Mbap(q, 1, new byte[] { 3, 2, 0x12, 0x34 }, 0)); }, delegate(ModbusMasterClient c, List<string> logs) { Read1234(c); }, 700, IPAddress.IPv6Loopback); });
            string simulatorPath = args.Length == 0 ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Modbus从机模拟器2.5.exe") : args[0];
            Check("simulator assembly available", delegate { Assert(File.Exists(simulatorPath), simulatorPath); });
            if (File.Exists(simulatorPath))
            {
                Assembly simulator = Assembly.LoadFrom(simulatorPath);
                foreach (int value in new int[] { 0, 4, 5, 6 }) { int mode = value; Check("simulator 2.5 all FCs / 20 immediate write-read cycles mode=" + mode, delegate { Simulator(mode, simulator); }); }
            }
            Console.WriteLine("MasterWireChecks: " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
}
