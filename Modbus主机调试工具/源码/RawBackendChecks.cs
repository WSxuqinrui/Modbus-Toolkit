using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

/// <summary>仅本机回环/注入通道的原始发送后端检查，不打开真实串口。</summary>
public static class RawBackendChecks
{
    private static int passed, failed;
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Check(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS raw backend " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL raw backend " + name + ": " + ex); }
    }
    private static void Wait(Func<bool> condition, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > 4000) throw new TimeoutException(message); Thread.Sleep(5); }
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("应拒绝：" + typeof(T).Name); }
    private static void Equal(byte[] expected, byte[] actual, string message)
    { Assert(BitConverter.ToString(expected) == BitConverter.ToString(actual), message); }
    private static byte[] ReadExact(Stream stream, int length)
    {
        byte[] bytes = new byte[length]; int offset = 0;
        while (offset < length) { int count = stream.Read(bytes, offset, length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
        return bytes;
    }
    private static object Field(object instance, string name)
    { return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
    private static IPEndPoint SlaveEndpoint(ModbusSimulator.SimulatorServer server, bool udp)
    { return udp ? (IPEndPoint)((UdpClient)Field(server, "udp")).Client.LocalEndPoint : (IPEndPoint)((TcpListener)Field(server, "listener")).LocalEndpoint; }
    private static string TargetFor(ModbusSimulator.SimulatorServer server, IPEndPoint endpoint)
    {
        foreach (string target in server.GetRawTargets()) if (target.Contains("[" + endpoint + "]")) return target;
        throw new InvalidOperationException("找不到实际端点 " + endpoint);
    }
    private static object TargetWriteGate(ModbusSimulator.SimulatorServer server, string name)
    {
        foreach (object target in ((IDictionary)Field(server, "tcpTargets")).Values)
            if ((string)target.GetType().GetField("Name").GetValue(target) == name) return target.GetType().GetField("WriteGate").GetValue(target);
        throw new InvalidOperationException("找不到目标发送锁");
    }

    public static int Main()
    {
        Check("API contract", delegate
        {
            Assert(typeof(ModbusSimulator.SimulatorServer).GetMethod("GetRawTargets") != null, "从机尚未提供具体原始发送目标");
            Assert(typeof(ModbusSimulator.SimulatorServer).GetMethod("SendRaw") != null, "从机尚未提供原始发送");
            Assert(typeof(ModbusMaster.ModbusMasterClient).GetMethod("ExchangeRaw") != null, "主机尚未提供不包装的原始交换");
            Assert(typeof(ModbusMaster.MasterSession).GetMethod("EnqueueRaw") != null, "主机会话尚未提供原始发送队列");
            Assert(typeof(ModbusMaster.MasterSession).GetProperty("IsRawMode") != null, "主机会话尚未提供原始模式保护");
        });
        Check("主机 TCP MBAP/RTU 模式逐字节直发，分段原样接收", MasterTcpBytes);
        Check("主机 UDP MBAP/RTU 模式逐字节直发", MasterUdpBytes);
        Check("主机无回包保持连接且不重试", MasterNoReply);
        Check("主机原始发送共用事务锁、深拷贝，真实 IO 故障关闭通道", MasterTransactionGateAndFault);
        Check("主机原始接收受总窗口约束，Dispose 可中断", MasterWindowAndDispose);
        Check("从机两个 TCP 对端必须明确选择，失效目标拒绝", SlaveTcpTargets);
        Check("从机自动回复与原始发送同锁，字节不交错且深拷贝", SlaveWriteLockAndCopy);
        Check("从机 Dispose 不等待发送锁，不记录失败 TX", SlaveDisposeDoesNotWaitForSendLock);
        Check("从机 UDP 仅实际来源、16项上限和目标淘汰", SlaveUdpTargets);
        Check("会话 pending 即停止轮询，保留旧队列且拒绝新 typed", SessionQueueAndRawMode);
        Check("会话原始数据不改变缓存及新鲜度，空回复不计错", SessionCacheIsolation);
        Check("会话 128 队列不丢单，满队列不切换模式", SessionQueueBound);
        Check("不支持原始接口的注入通道不进入原始模式", SessionUnsupported);
        Console.WriteLine("RawBackendChecks: " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    private static void MasterTcpBytes()
    {
        foreach (int mode in new int[] { 0, 5 })
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            ManualResetEvent release = new ManualResetEvent(false); Exception peerError = null;
            byte[] raw = { 0, 255, 1, 13, 10, 0xA5 }, response = { 0xFF, 0, 0x41, 0x80, 13, 10 };
            Thread peer = new Thread(delegate()
            {
                try
                {
                    using (TcpClient accepted = listener.AcceptTcpClient())
                    {
                        accepted.ReceiveTimeout = 1500; NetworkStream stream = accepted.GetStream();
                        Equal(raw, ReadExact(stream, raw.Length), "主机不得添加 MBAP/CRC/换行");
                        stream.Write(response, 0, 3); Thread.Sleep(15); stream.Write(response, 3, 3);
                        release.WaitOne(3000);
                        Assert(accepted.Available == 0, "原始发送不得自动重试或补发");
                    }
                }
                catch (Exception ex) { peerError = ex; }
            }); peer.IsBackground = true; peer.Start();
            List<string> logs = new List<string>();
            try
            {
                using (ModbusMaster.ModbusMasterClient client = new ModbusMaster.ModbusMasterClient(new ModbusMaster.MasterConnectionOptions { Mode = mode, Port = ((IPEndPoint)listener.LocalEndpoint).Port }, logs.Add))
                {
                    client.Connect(); Stopwatch timer = Stopwatch.StartNew();
                    Equal(response, client.ExchangeRaw(raw, 1000), "原始接收必须拼合每段未经校验的字节");
                    Assert(timer.ElapsedMilliseconds < 600 && client.IsConnected, "首字节后静默应提前返回并保持连接");
                    Assert(logs.Exists(delegate(string entry) { return entry.StartsWith("TX: RAW "); }), "成功发送记录 TX: RAW");
                    Assert(logs.FindAll(delegate(string entry) { return entry.StartsWith("RX: RAW "); }).Count >= 1, "每个收到的字节段记录 RX: RAW");
                    Throws<ArgumentOutOfRangeException>(delegate { client.ExchangeRaw(new byte[0], 100); });
                    Throws<ArgumentOutOfRangeException>(delegate { client.ExchangeRaw(new byte[8193], 100); });
                    Throws<ArgumentOutOfRangeException>(delegate { client.ExchangeRaw(raw, 99); });
                    Throws<ArgumentOutOfRangeException>(delegate { client.ExchangeRaw(raw, 5001); });
                    Assert(client.IsConnected, "参数校验失败不得关闭已有连接"); release.Set();
                }
            }
            finally { release.Set(); peer.Join(2000); listener.Stop(); release.Dispose(); }
            if (peerError != null) throw peerError;
        }
    }

    private static void MasterUdpBytes()
    {
        foreach (int mode in new int[] { 4, 6 })
        {
            using (UdpClient peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                byte[] request = new byte[8192]; for (int i = 0; i < request.Length; i++) request[i] = (byte)(i * 13);
                byte[] reply = { 0, 255, 128, 13, 10 }; Exception peerError = null;
                Thread worker = new Thread(delegate()
                {
                    try { peer.Client.ReceiveTimeout = 2000; IPEndPoint sender = null; Equal(request, peer.Receive(ref sender), "UDP 必须保持单个最终数据报"); peer.Send(reply, reply.Length, sender); }
                    catch (Exception ex) { peerError = ex; }
                }); worker.IsBackground = true; worker.Start();
                using (ModbusMaster.ModbusMasterClient client = new ModbusMaster.ModbusMasterClient(new ModbusMaster.MasterConnectionOptions { Mode = mode, Port = ((IPEndPoint)peer.Client.LocalEndPoint).Port }, null))
                { client.Connect(); Equal(reply, client.ExchangeRaw(request, 500), "UDP 原始回包不得解释为 MBAP/CRC"); Assert(client.IsConnected, "原始 UDP 回包不校验协议"); }
                Assert(worker.Join(2500), "UDP 回环线程结束"); if (peerError != null) throw peerError;
            }
        }
    }

    private static void MasterNoReply()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        ManualResetEvent release = new ManualResetEvent(false); Exception peerError = null;
        Thread peer = new Thread(delegate()
        {
            try { using (TcpClient accepted = listener.AcceptTcpClient()) { accepted.ReceiveTimeout = 2000; Equal(new byte[] { 0x11, 0x22 }, ReadExact(accepted.GetStream(), 2), "只发送两次调用各自字节"); release.WaitOne(3000); Assert(accepted.Available == 0, "无回包时不得重试"); } }
            catch (Exception ex) { peerError = ex; }
        }); peer.IsBackground = true; peer.Start();
        try
        {
            using (ModbusMaster.ModbusMasterClient client = new ModbusMaster.ModbusMasterClient(new ModbusMaster.MasterConnectionOptions { Port = ((IPEndPoint)listener.LocalEndpoint).Port }, null))
            {
                client.Connect(); Stopwatch timer = Stopwatch.StartNew();
                Assert(client.ExchangeRaw(new byte[] { 0x11 }, 120).Length == 0 && client.IsConnected, "无回包返回空且不关闭");
                Assert(timer.ElapsedMilliseconds >= 100 && timer.ElapsedMilliseconds < 800, "未收到首字节须使用完整接收窗口");
                Assert(client.ExchangeRaw(new byte[] { 0x22 }, 120).Length == 0 && client.IsConnected, "同连接允许再次原始发送"); release.Set();
            }
        }
        finally { release.Set(); peer.Join(2000); listener.Stop(); release.Dispose(); }
        if (peerError != null) throw peerError;
    }

    private static void MasterWindowAndDispose()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using (ModbusMaster.ModbusMasterClient client = new ModbusMaster.ModbusMasterClient(new ModbusMaster.MasterConnectionOptions { Port = ((IPEndPoint)listener.LocalEndpoint).Port }, null))
        {
            client.Connect();
            using (TcpClient peer = listener.AcceptTcpClient())
            {
                Exception failure = null; byte[] received = null;
                Thread worker = new Thread(delegate() { try { received = client.ExchangeRaw(new byte[] { 1 }, 120); } catch (Exception ex) { failure = ex; } }); worker.Start();
                peer.ReceiveTimeout = 1000; ReadExact(peer.GetStream(), 1); Stopwatch timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 200) { peer.GetStream().Write(new byte[] { 0xA5 }, 0, 1); Thread.Sleep(20); }
                Assert(worker.Join(1000) && failure == null && received.Length > 0, "持续收字节时也必须在总窗口结束");
                Assert(client.IsConnected, "接收窗口结束保持通道");
            }
        }
        listener.Stop();
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        ModbusMaster.ModbusMasterClient cancellable = new ModbusMaster.ModbusMasterClient(new ModbusMaster.MasterConnectionOptions { Port = ((IPEndPoint)listener.LocalEndpoint).Port }, null);
        cancellable.Connect();
        using (TcpClient peer = listener.AcceptTcpClient())
        {
            Exception stopped = null;
            Thread worker = new Thread(delegate() { try { cancellable.ExchangeRaw(new byte[] { 2 }, 5000); } catch (Exception ex) { stopped = ex; } }); worker.Start();
            peer.ReceiveTimeout = 1000; ReadExact(peer.GetStream(), 1); Stopwatch timer = Stopwatch.StartNew(); cancellable.Dispose();
            Assert(worker.Join(700) && timer.ElapsedMilliseconds < 700 && stopped != null, "Dispose 应中断长接收窗口");
        }
        listener.Stop();
    }

    private static void MasterTransactionGateAndFault()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using (ModbusMaster.ModbusMasterClient client = new ModbusMaster.ModbusMasterClient(new ModbusMaster.MasterConnectionOptions { Port = ((IPEndPoint)listener.LocalEndpoint).Port }, null))
        {
            client.Connect();
            using (TcpClient peer = listener.AcceptTcpClient())
            {
                peer.ReceiveTimeout = 1000; object transactionGate = Field(client, "transactionGate"); byte[] raw = { 0x21, 0x43 };
                Exception error = null; Thread worker = new Thread(delegate() { try { client.ExchangeRaw(raw, 100); } catch (Exception ex) { error = ex; } });
                Monitor.Enter(transactionGate);
                try
                {
                    worker.Start(); Wait(delegate { return (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0; }, "原始发送必须等待已有事务锁");
                    raw[0] = 0xFF; Assert(peer.Available == 0, "持有事务锁时不允许直接写入 socket");
                }
                finally { Monitor.Exit(transactionGate); }
                Equal(new byte[] { 0x21, 0x43 }, ReadExact(peer.GetStream(), 2), "直接原始 API 也必须深拷贝调用方数组");
                Assert(worker.Join(1500) && error == null && client.IsConnected, "正常无回包不应关闭连接");
                Thread faulted = new Thread(delegate() { try { client.ExchangeRaw(new byte[] { 0x65 }, 1000); } catch (Exception ex) { error = ex; } }); faulted.Start();
                ReadExact(peer.GetStream(), 1); peer.Close();
                Assert(faulted.Join(1500) && error != null && !client.IsConnected, "真实 TCP 关闭必须作为 IO 故障清理通道");
            }
        }
        listener.Stop();
    }

    private static void SlaveTcpTargets()
    {
        foreach (bool rtu in new bool[] { false, true })
        {
            using (ModbusSimulator.SimulatorServer server = new ModbusSimulator.SimulatorServer(new ModbusSimulator.RegisterBank(), 1, null))
            {
                if (rtu) server.StartRtuTcp(IPAddress.Loopback, 0); else server.StartTcp(IPAddress.Loopback, 0);
                Assert(server.GetRawTargets().Length == 0, "没有连接时不能虚构 TCP 目标");
                using (TcpClient first = new TcpClient()) using (TcpClient second = new TcpClient())
                {
                    first.Connect(SlaveEndpoint(server, false)); second.Connect(SlaveEndpoint(server, false));
                    first.ReceiveTimeout = second.ReceiveTimeout = 1200;
                    Wait(delegate { return server.GetRawTargets().Length == 2; }, "两个 TCP 目标登记");
                    string firstTarget = TargetFor(server, (IPEndPoint)first.Client.LocalEndPoint), secondTarget = TargetFor(server, (IPEndPoint)second.Client.LocalEndPoint);
                    byte[] firstBytes = { 1, 0, 255, 13, 10 }, secondBytes = { 0x51, 0x42, 0x33 };
                    server.SendRaw(firstTarget, firstBytes); Equal(firstBytes, ReadExact(first.GetStream(), firstBytes.Length), "必须只发给首个具体对端");
                    Assert(second.Available == 0, "第二个连接不能收到广播");
                    server.SendRaw(secondTarget, secondBytes); Equal(secondBytes, ReadExact(second.GetStream(), secondBytes.Length), "必须可选择第二个具体对端");
                    Assert(first.Available == 0, "首个连接不能收到第二个对端的内容");
                    Throws<ArgumentOutOfRangeException>(delegate { server.SendRaw(secondTarget, new byte[0]); });
                    Throws<ArgumentOutOfRangeException>(delegate { server.SendRaw(secondTarget, new byte[8193]); });
                    first.Close(); Wait(delegate { return server.GetRawTargets().Length == 1; }, "断开的目标应删除");
                    Throws<InvalidOperationException>(delegate { server.SendRaw(firstTarget, firstBytes); });
                    Throws<InvalidOperationException>(delegate { server.SendRaw("127.0.0.1:1", firstBytes); });
                }
            }
        }
    }

    private static void SlaveWriteLockAndCopy()
    {
        using (ModbusSimulator.SimulatorServer server = new ModbusSimulator.SimulatorServer(new ModbusSimulator.RegisterBank(), 1, null))
        using (TcpClient peer = new TcpClient())
        {
            server.StartTcp(IPAddress.Loopback, 0); peer.Connect(SlaveEndpoint(server, false)); peer.ReceiveTimeout = 1500;
            Wait(delegate { return server.GetRawTargets().Length == 1; }, "登记 TCP 目标");
            string target = server.GetRawTargets()[0]; object writeGate = TargetWriteGate(server, target); byte[] raw = new byte[8192];
            for (int i = 0; i < raw.Length; i++) raw[i] = 0xAA;
            Exception error = null; Thread sending = new Thread(delegate() { try { server.SendRaw(target, raw); } catch (Exception ex) { error = ex; } });
            Monitor.Enter(writeGate);
            try
            {
                sending.Start(); Wait(delegate { return (sending.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0; }, "原始发送等待每目标锁");
                raw[0] = 0xBB;
                byte[] request = { 0, 7, 0, 0, 0, 6, 1, 3, 0, 0, 0, 1 }; peer.GetStream().Write(request, 0, request.Length);
                Wait(delegate { return server.Requests == 1; }, "协议回复进入同一发送锁");
                Assert(peer.Available == 0, "自动回复不能绕过原始发送使用的目标锁");
                Assert(server.GetRawTargets().Length == 1, "等待发送不能占住全局目标查询锁");
            }
            finally { Monitor.Exit(writeGate); }
            Assert(sending.Join(1500) && error == null, "原始发送应成功");
            byte[] bytes = ReadExact(peer.GetStream(), 8192 + 11); int rawOffset = bytes[0] == 0xAA ? 0 : 11;
            for (int i = 0; i < 8192; i++) Assert(bytes[rawOffset + i] == 0xAA, "深拷贝内容必须连续、不能被回复插入");
            int ack = rawOffset == 0 ? 8192 : 0;
            byte[] ackBytes = new byte[11]; Buffer.BlockCopy(bytes, ack, ackBytes, 0, 11);
            Equal(new byte[] { 0, 7, 0, 0, 0, 5, 1, 3, 2, 0, 0 }, ackBytes, "自动 MBAP 回复保持完整");
        }
    }

    private static void SlaveDisposeDoesNotWaitForSendLock()
    {
        List<string> logs = new List<string>(); object logGate = new object();
        ModbusSimulator.SimulatorServer server = new ModbusSimulator.SimulatorServer(new ModbusSimulator.RegisterBank(), 1, delegate(string entry) { lock (logGate) logs.Add(entry); });
        using (TcpClient peer = new TcpClient())
        {
            server.StartTcp(IPAddress.Loopback, 0); peer.Connect(SlaveEndpoint(server, false));
            Wait(delegate { return server.GetRawTargets().Length == 1; }, "登记 TCP 目标");
            string target = server.GetRawTargets()[0]; object writeGate = TargetWriteGate(server, target); Exception error = null;
            Thread sending = new Thread(delegate() { try { server.SendRaw(target, new byte[] { 0x42 }); } catch (Exception ex) { error = ex; } });
            Monitor.Enter(writeGate);
            try
            {
                sending.Start(); Wait(delegate { return (sending.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0; }, "发送等待锁");
                Stopwatch timer = Stopwatch.StartNew(); server.Dispose();
                Assert(timer.ElapsedMilliseconds < 1000, "Dispose 不得等待被占住的目标发送锁");
            }
            finally { Monitor.Exit(writeGate); }
            Assert(sending.Join(1000) && error is InvalidOperationException, "关闭后等待中的发送必须拒绝");
            lock (logGate) Assert(!logs.Exists(delegate(string entry) { return entry.StartsWith("TX: RAW "); }), "未发送成功不能记录 TX: RAW");
        }
    }

    private static void SlaveUdpTargets()
    {
        foreach (bool rtu in new bool[] { false, true })
        using (ModbusSimulator.SimulatorServer server = new ModbusSimulator.SimulatorServer(new ModbusSimulator.RegisterBank(), 1, null))
        {
            server.StartUdp(IPAddress.Loopback, 0, rtu); Assert(server.GetRawTargets().Length == 0, "UDP 未收到来源前没有发送目标");
            List<UdpClient> peers = new List<UdpClient>(); string evicted = null;
            try
            {
                for (int i = 0; i < 17; i++)
                {
                    UdpClient peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); peers.Add(peer); peer.Client.ReceiveTimeout = 1200;
                    peer.Send(new byte[] { 0xA5 }, 1, SlaveEndpoint(server, true)); int expected = Math.Min(16, i + 1);
                    Wait(delegate { return server.GetRawTargets().Length == expected && Array.Exists(server.GetRawTargets(), delegate(string entry) { return entry.Contains("[" + peer.Client.LocalEndPoint + "]"); }); }, "实际 UDP 来源登记");
                    if (i == 0) evicted = server.GetRawTargets()[0];
                }
                Assert(server.GetRawTargets().Length == 16, "UDP 来源必须有界");
                Throws<InvalidOperationException>(delegate { server.SendRaw(evicted, new byte[] { 1 }); });
                Throws<InvalidOperationException>(delegate { server.SendRaw("UDP [127.0.0.1:1]", new byte[] { 1 }); });
                string selected = TargetFor(server, (IPEndPoint)peers[16].Client.LocalEndPoint); byte[] bytes = { 0, 255, 13, 10 };
                server.SendRaw(selected, bytes); IPEndPoint sender = null; Equal(bytes, peers[16].Receive(ref sender), "UDP 必须只发送到选中的实际来源");
                for (int i = 0; i < 16; i++) Assert(peers[i].Available == 0, "UDP 不能广播给其它来源");
            }
            finally { foreach (UdpClient peer in peers) peer.Close(); }
        }
    }

    private sealed class FakeRawConnection : ModbusMaster.IMasterConnection, ModbusMaster.IRawMasterConnection
    {
        public readonly ModbusMaster.RegisterBank Remote;
        public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(true);
        public bool BlockTyped, BlockRaw, EmptyReply;
        private volatile bool connected;
        private readonly object gate = new object();
        private readonly List<string> calls = new List<string>();
        private readonly List<byte[]> raws = new List<byte[]>();
        public FakeRawConnection(ModbusMaster.RegisterBank bank) { Remote = bank; }
        public bool IsConnected { get { return connected; } }
        public void Connect() { connected = true; }
        public byte[] Execute(byte unit, byte[] pdu)
        {
            lock (gate) calls.Add("PDU " + pdu[0]);
            if (BlockTyped) { Entered.Set(); Release.WaitOne(); }
            if (!connected) throw new IOException("测试通道已关闭");
            return Remote.Process(pdu);
        }
        public byte[] ExchangeRaw(byte[] bytes, int receiveWindowMs)
        {
            lock (gate) { calls.Add("RAW"); raws.Add((byte[])bytes.Clone()); }
            if (BlockRaw) { Entered.Set(); Release.WaitOne(); }
            if (!connected) throw new IOException("测试通道已关闭");
            return EmptyReply ? new byte[0] : new byte[] { 1, 3, 2, 0xFF, 0xFF };
        }
        public string[] Calls() { lock (gate) return calls.ToArray(); }
        public byte[][] Raws() { lock (gate) return raws.ToArray(); }
        public void Dispose() { connected = false; Release.Set(); }
    }
    private sealed class TypedOnlyConnection : ModbusMaster.IMasterConnection
    {
        public bool IsConnected { get; private set; }
        public void Connect() { IsConnected = true; }
        public byte[] Execute(byte unit, byte[] pdu) { return new byte[] { 3, 2, 0, 0 }; }
        public void Dispose() { IsConnected = false; }
    }
    private static ModbusMaster.RegisterBank Bank()
    {
        ModbusMaster.RegisterBank bank = new ModbusMaster.RegisterBank();
        for (int area = 0; area < 4; area++) bank.Configure(area, 0, area == 2 ? 1 : 0);
        return bank;
    }
    private static ModbusMaster.MasterSession Session(ModbusMaster.RegisterBank bank, ModbusMaster.IMasterConnection connection)
    {
        ModbusMaster.MasterSession session = new ModbusMaster.MasterSession(bank, new ModbusMaster.MasterConnectionOptions(), null, connection);
        session.Start(); Wait(delegate { return session.IsConnected && !session.IsBusy; }, "假通道后台连接"); return session;
    }
    private static void SessionQueueAndRawMode()
    {
        ModbusMaster.RegisterBank cache = Bank(); FakeRawConnection channel = new FakeRawConnection(Bank());
        channel.BlockTyped = true; channel.Release.Reset();
        using (ModbusMaster.MasterSession session = Session(cache, channel))
        {
            session.ReadOnce(); Wait(delegate { return channel.Entered.WaitOne(0); }, "阻塞首条类型化读取");
            session.EnqueueWrite(2, 0, new ushort[] { 33 }, false); session.ReadOnce(); session.SetPolling(true, 50);
            byte[] raw = { 0x44, 0x55 }; session.EnqueueRaw(raw, 100); raw[0] = 0xAA;
            Assert(session.IsRawMode && !session.IsPolling && session.StateText.Contains("重新连接恢复Modbus"), "pending 入队后立即锁定类型化操作并停轮询");
            Throws<InvalidOperationException>(session.ReadOnce);
            Throws<InvalidOperationException>(delegate { session.EnqueueWrite(2, 0, new ushort[] { 44 }, false); });
            Throws<InvalidOperationException>(delegate { session.SetPolling(true, 50); });
            session.SetPolling(false, 50); channel.Release.Set();
            Wait(delegate { return channel.Raws().Length == 1 && !session.IsBusy; }, "旧队列和原始发送全部完成");
            Assert(String.Join(",", channel.Calls()) == "PDU 3,PDU 6,PDU 3,PDU 3,RAW", "先前已经入队的读写应按顺序完成且不能偷偷恢复轮询");
            Equal(new byte[] { 0x44, 0x55 }, channel.Raws()[0], "原始队列深拷贝");
            int before = channel.Calls().Length; Thread.Sleep(150); Assert(channel.Calls().Length == before, "原始模式不能继续轮询");
            session.EnqueueRaw(new byte[] { 0x66 }, 100); Wait(delegate { return channel.Raws().Length == 2 && !session.IsBusy; }, "同连接第二次原始发送");
            Assert(cache.Get(2, 0) == 33 && session.Errors == 0, "原始数据不能改写旧队列中合法回读的值");
        }
    }
    private static void SessionCacheIsolation()
    {
        foreach (bool previouslyRead in new bool[] { false, true })
        {
            ModbusMaster.RegisterBank cache = Bank(), remote = Bank(); remote.Set(2, 0, 123); cache.Set(2, 0, 7);
            FakeRawConnection channel = new FakeRawConnection(remote);
            using (ModbusMaster.MasterSession session = Session(cache, channel))
            {
                if (previouslyRead) { session.ReadOnce(); Wait(delegate { return session.AreaHasFreshData(2) && !session.IsBusy; }, "协议读取完成"); }
                string status = session.GetAreaStatus(2); long revision = cache.Revision; ushort value = cache.Get(2, 0);
                session.EnqueueRaw(new byte[] { 0x33 }, 100); Wait(delegate { return channel.Raws().Length == 1 && !session.IsBusy; }, "原始回包完成");
                channel.EmptyReply = true; session.EnqueueRaw(new byte[] { 0x44 }, 100); Wait(delegate { return channel.Raws().Length == 2 && !session.IsBusy; }, "空回包完成");
                Assert(cache.Get(2, 0) == value && cache.Revision == revision, "原始回包不得提交缓存");
                Assert(session.AreaHasFreshData(2) == previouslyRead && session.GetAreaStatus(2) == status, "原始回包不得改变数据区时间戳及新鲜度");
                Assert(session.Errors == 0 && session.IsConnected && session.IsRawMode, "空回包不计错、不掉线、不退出 rawmode");
            }
        }
    }
    private static void SessionQueueBound()
    {
        FakeRawConnection channel = new FakeRawConnection(Bank()); channel.BlockRaw = true; channel.Release.Reset();
        using (ModbusMaster.MasterSession session = Session(Bank(), channel))
        {
            session.EnqueueRaw(new byte[] { 1 }, 100); Wait(delegate { return channel.Entered.WaitOne(0); }, "阻塞首条原始交换");
            for (int i = 0; i < 128; i++) session.EnqueueRaw(new byte[] { (byte)i }, 100);
            Throws<InvalidOperationException>(delegate { session.EnqueueRaw(new byte[] { 0xFF }, 100); });
            channel.Release.Set(); Wait(delegate { return channel.Raws().Length == 129 && !session.IsBusy; }, "已授权的128条命令不得丢失");
            byte[][] sent = channel.Raws(); for (int i = 0; i < 128; i++) Assert(sent[i + 1][0] == (byte)i, "原始命令必须保持 FIFO");
        }
        channel = new FakeRawConnection(Bank()); channel.BlockTyped = true; channel.Release.Reset();
        using (ModbusMaster.MasterSession session = Session(Bank(), channel))
        {
            session.ReadOnce(); Wait(delegate { return channel.Entered.WaitOne(0); }, "阻塞类型化任务"); session.SetPolling(true, 50);
            for (int i = 0; i < 128; i++) session.EnqueueWrite(2, 0, new ushort[] { (ushort)i }, false);
            Throws<InvalidOperationException>(delegate { session.EnqueueRaw(new byte[] { 1 }, 100); });
            Assert(!session.IsRawMode && session.IsPolling, "满队列拒绝原始请求时不能先切换 rawmode 或停止轮询");
        }
    }
    private static void SessionUnsupported()
    {
        using (ModbusMaster.MasterSession session = Session(Bank(), new TypedOnlyConnection()))
        {
            Throws<NotSupportedException>(delegate { session.EnqueueRaw(new byte[] { 1 }, 100); });
            Assert(!session.IsRawMode, "没有原始接口的通道不能进入 rawmode");
            session.ReadOnce(); Wait(delegate { return !session.IsBusy && session.AreaHasFreshData(2); }, "普通通道仍允许协议读取");
        }
    }
}
