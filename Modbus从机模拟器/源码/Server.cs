using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ModbusSimulator
{
    public sealed class SimulatorServer : IDisposable
    {
        public readonly RegisterBank Bank;
        private readonly Action<string> log;
        private readonly byte unit;
        private readonly object gate = new object();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly Dictionary<TcpClient, RawTarget> tcpTargets = new Dictionary<TcpClient, RawTarget>();
        private readonly List<RawTarget> udpTargets = new List<RawTarget>();
        private RawTarget serialTarget;
        private int nextRawTarget;
        private sealed class RawTarget
        {
            public string Name;
            public TcpClient Tcp;
            public UdpClient Datagram;
            public IPEndPoint Peer;
            public SerialPort Port;
            public readonly object WriteGate = new object();
        }
        private TcpListener listener;
        private UdpClient udp;
        private Thread networkThread;
        private bool rtuTcp;
        private SerialPort serial;
        private volatile bool running;
        private long requests;
        public long Requests { get { return Interlocked.Read(ref requests); } }
        public bool IsRunning { get { return running; } }
        public SimulatorServer(RegisterBank bank, byte unitId, Action<string> logger)
        { Bank = bank; unit = unitId; log = logger; }
        private void Log(string text) { if (log != null) log(text); }
        private void FrameLog(string direction, byte[] frame)
        {
            // 在方向后固定加冒号；TCP 对端信息仍保留在方向后。
            string prefix = direction.Length > 2 ? direction.Substring(0, 2) + ":" + direction.Substring(2) : direction + ":";
            Log(prefix + " " + BitConverter.ToString(frame).Replace('-', ' '));
        }
        private void AsciiFrameLog(string direction, byte[] frame)
        {
            string readable = Encoding.ASCII.GetString(frame).Replace("\r", "\\r").Replace("\n", "\\n");
            Log(direction + ": " + readable + " | HEX " + BitConverter.ToString(frame).Replace('-', ' '));
        }

        /// <summary>只列出本次服务中真实存在的目标；UDP 来源按最近活动保留最多 16 个。</summary>
        public string[] GetRawTargets()
        {
            lock (gate)
            {
                if (!running) return new string[0];
                List<string> result = new List<string>();
                if (serialTarget != null) result.Add(serialTarget.Name);
                foreach (TcpClient client in clients)
                {
                    RawTarget target;
                    if (tcpTargets.TryGetValue(client, out target)) result.Add(target.Name);
                }
                foreach (RawTarget target in udpTargets) result.Add(target.Name);
                return result.ToArray();
            }
        }

        /// <summary>
        /// 发送已经拼好的最终字节。必须选定一个仍存在的目标；不广播、不添加协议头或校验。
        /// 与该目标的自动 Modbus 回复共用发送锁；全局 gate 只查询目标，不覆盖阻塞 IO。
        /// </summary>
        public void SendRaw(string target, byte[] data)
        {
            if (String.IsNullOrEmpty(target)) throw new ArgumentException("请先选择一个实际连接或报文来源。", "target");
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length < 1 || data.Length > 8192) throw new ArgumentOutOfRangeException("data", "原始发送长度必须为 1～8192 字节。");
            byte[] copy = (byte[])data.Clone(); RawTarget selected = null;
            lock (gate)
            {
                if (!running) throw new InvalidOperationException("服务尚未运行。");
                if (serialTarget != null && serialTarget.Name == target) selected = serialTarget;
                foreach (RawTarget candidate in tcpTargets.Values) if (candidate.Name == target) { selected = candidate; break; }
                if (selected == null) foreach (RawTarget candidate in udpTargets) if (candidate.Name == target) { selected = candidate; break; }
                if (selected == null) throw new InvalidOperationException("发送目标已不存在，请刷新并重新选择。");
            }
            lock (selected.WriteGate)
            {
                if (!IsCurrentTarget(selected)) throw new InvalidOperationException("发送目标已断开或失效，请重新选择。");
                if (selected.Tcp != null) selected.Tcp.GetStream().Write(copy, 0, copy.Length);
                else if (selected.Datagram != null) selected.Datagram.Send(copy, copy.Length, selected.Peer);
                else selected.Port.Write(copy, 0, copy.Length);
                FrameLog("TX RAW [" + selected.Name + "]", copy);
            }
        }

        private bool IsCurrentTarget(RawTarget target)
        {
            lock (gate)
            {
                if (!running) return false;
                if (target.Tcp != null)
                {
                    RawTarget current;
                    return tcpTargets.TryGetValue(target.Tcp, out current) && Object.ReferenceEquals(target, current);
                }
                if (target.Datagram != null) return Object.ReferenceEquals(target.Datagram, udp) && udpTargets.Contains(target);
                return Object.ReferenceEquals(target, serialTarget) && Object.ReferenceEquals(target.Port, serial);
            }
        }

        private bool WriteTcp(TcpClient client, NetworkStream stream, byte[] bytes)
        {
            RawTarget target; lock (gate) if (!tcpTargets.TryGetValue(client, out target)) return false;
            lock (target.WriteGate)
            {
                if (!IsCurrentTarget(target)) return false;
                stream.Write(bytes, 0, bytes.Length); return true;
            }
        }

        private bool WriteSerial(byte[] bytes)
        {
            RawTarget target; lock (gate) target = serialTarget;
            if (target == null) return false;
            lock (target.WriteGate)
            {
                if (!IsCurrentTarget(target)) return false;
                target.Port.Write(bytes, 0, bytes.Length); return true;
            }
        }

        private RawTarget RememberUdpPeer(IPEndPoint peer)
        {
            lock (gate)
            {
                if (!running) return null;
                RawTarget target = null;
                foreach (RawTarget candidate in udpTargets) if (candidate.Peer.Equals(peer)) { target = candidate; break; }
                if (target == null)
                    target = new RawTarget { Datagram = udp, Peer = new IPEndPoint(peer.Address, peer.Port), Name = "UDP [" + peer + "] #" + (++nextRawTarget) };
                else udpTargets.Remove(target);
                udpTargets.Add(target);
                if (udpTargets.Count > 16) udpTargets.RemoveAt(0);
                return target;
            }
        }
        public void StartTcp(IPAddress address, int port)
        {
            if (running) throw new InvalidOperationException("服务已启动。");
            listener = new TcpListener(address, port);
            listener.Start(16);
            running = true;
            rtuTcp = false;
            networkThread = new Thread(AcceptLoop) { IsBackground = true }; networkThread.Start();
            Log("TCP 监听 " + address + ":" + port + "，站号 " + unit);
        }
        /// <summary>UDP 每个数据报恰好包含一帧；rtu=false 使用 MBAP，true 使用原始 RTU+CRC。</summary>
        public void StartUdp(IPAddress address, int port, bool rtu)
        {
            if (running) throw new InvalidOperationException("服务已启动。");
            udp = new UdpClient(new IPEndPoint(address, port));
            running = true;
            networkThread = new Thread(delegate() { UdpLoop(rtu); }) { IsBackground = true }; networkThread.Start();
            Log("UDP/" + (rtu ? "RTU" : "MBAP") + " 监听 " + udp.Client.LocalEndPoint + "，站号 " + unit);
        }
        /// <summary>TCP 传输原始 RTU 帧，保留地址和 CRC；支持持久连接、拆包和粘包。</summary>
        public void StartRtuTcp(IPAddress address, int port)
        {
            if (running) throw new InvalidOperationException("服务已启动。");
            listener = new TcpListener(address, port); listener.Start(16);
            rtuTcp = true; running = true;
            networkThread = new Thread(AcceptLoop) { IsBackground = true }; networkThread.Start();
            Log("TCP/RTU 监听 " + listener.LocalEndpoint + "，站号 " + unit);
        }
        private void UdpLoop(bool rtu)
        {
            while (running)
            {
                try
                {
                    IPEndPoint peer = null;
                    byte[] frame = udp.Receive(ref peer);
                    RawTarget target = RememberUdpPeer(peer);
                    string context = " UDP/" + (rtu ? "RTU" : "MBAP") + " [" + peer + "]";
                    byte[] reply = rtu ? ProcessRtuCore(frame, context) : ProcessMbapCore(frame, context, true);
                    if (reply != null && target != null)
                    {
                        lock (target.WriteGate)
                        {
                            if (!IsCurrentTarget(target)) continue;
                            target.Datagram.Send(reply, reply.Length, target.Peer);
                            FrameLog("TX" + context, reply);
                        }
                    }
                }
                catch (SocketException ex) { if (running) { Log("UDP 服务已停止：" + ex.Message); running = false; } break; }
                catch (ObjectDisposedException) { break; }
            }
        }
        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (gate)
                    {
                        if (!running || clients.Count >= 16) { client.Close(); continue; }
                        clients.Add(client);
                        tcpTargets.Add(client, new RawTarget { Tcp = client, Name = (rtuTcp ? "TCP/RTU" : "TCP") + " [" + client.Client.RemoteEndPoint + "] #" + (++nextRawTarget) });
                    }
                    client.NoDelay = true;
                    client.ReceiveTimeout = 120000;
                    client.SendTimeout = 3000;
                    ThreadPool.QueueUserWorkItem(delegate { if (rtuTcp) ServeRtuClient(client); else ServeClient(client); });
                }
                catch (SocketException ex) { if (running) { Log("监听失败：" + ex.Message); running = false; } break; }
                catch (ObjectDisposedException) { break; }
            }
        }
        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] bytes = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(bytes, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return bytes;
        }
        private void ServeClient(TcpClient client)
        {
            string peer = "TCP";
            try
            {
                peer = client.Client.RemoteEndPoint.ToString();
                Log("连接 " + peer);
                using (NetworkStream stream = client.GetStream())
                {
                    while (running)
                    {
                        // 按 MBAP 长度读取，可处理 TCP 拆包、粘包和持续连接。
                        byte[] header = ReadExact(stream, 6);
                        if (header == null) break;
                        int length = RegisterBank.Word(header, 4);
                        if (RegisterBank.Word(header, 2) != 0 || length < 2 || length > 254)
                        { Log("丢弃非法 MBAP 并断开 " + peer); break; }
                        byte[] body = ReadExact(stream, length);
                        if (body == null) break;
                        byte[] frame = new byte[6 + length];
                        Buffer.BlockCopy(header, 0, frame, 0, 6);
                        Buffer.BlockCopy(body, 0, frame, 6, length);
                        byte[] reply = ProcessMbap(frame);
                        if (reply != null && WriteTcp(client, stream, reply))
                        {
                            FrameLog("TX [" + peer + "]", reply);
                            Log("已写入 TCP TID=" + RegisterBank.Word(reply, 0) + " FC=0x" + reply[7].ToString("X2"));
                        }
                    }
                }
            }
            catch (IOException ex) { if (running) Log(peer + "：" + ex.Message); }
            catch (SocketException ex) { if (running) Log(peer + "：" + ex.Message); }
            catch (ObjectDisposedException) { }
            finally
            {
                lock (gate) { clients.Remove(client); tcpTargets.Remove(client); Monitor.PulseAll(gate); }
                client.Close();
                Log("断开 " + peer);
            }
        }
        public byte[] ProcessMbap(byte[] frame)
        { return ProcessMbapCore(frame, "", false); }
        private byte[] ProcessMbapCore(byte[] frame, string context, bool allowBroadcast)
        {
            if (frame == null || frame.Length < 8 || frame.Length > 260 || RegisterBank.Word(frame, 2) != 0 ||
                RegisterBank.Word(frame, 4) != frame.Length - 6) return null;
            FrameLog("RX" + context, frame);
            bool broadcast = allowBroadcast && frame[6] == 0;
            if (!broadcast && frame[6] != unit) { Log("忽略不匹配站号 " + frame[6]); return null; }
            if (broadcast && frame[7] != 5 && frame[7] != 6 && frame[7] != 15 && frame[7] != 16) return null;
            byte[] pdu = new byte[frame.Length - 7];
            Buffer.BlockCopy(frame, 7, pdu, 0, pdu.Length);
            byte[] response = Bank.Process(pdu);
            Interlocked.Increment(ref requests);
            if (broadcast) { Log("已处理 UDP 广播写入，不回复"); return null; }
            byte[] reply = new byte[7 + response.Length];
            Buffer.BlockCopy(frame, 0, reply, 0, 7);
            reply[4] = (byte)((response.Length + 1) >> 8);
            reply[5] = (byte)(response.Length + 1);
            Buffer.BlockCopy(response, 0, reply, 7, response.Length);
            return reply;
        }
        private void ServeRtuClient(TcpClient client)
        {
            string peer = "TCP/RTU";
            try
            {
                peer = client.Client.RemoteEndPoint.ToString(); Log("连接 TCP/RTU " + peer);
                using (NetworkStream stream = client.GetStream())
                {
                    RtuStreamParser parser = new RtuStreamParser(); byte[] incoming = new byte[1024];
                    while (running)
                    {
                        int count = stream.Read(incoming, 0, incoming.Length); if (count == 0) break;
                        for (int i = 0; i < count && running; i++)
                        {
                            byte[] frame = parser.Feed(incoming[i]); if (frame == null) continue;
                            string context = " TCP/RTU [" + peer + "]";
                            byte[] reply = ProcessRtuCore(frame, context);
                            if (reply != null && WriteTcp(client, stream, reply)) FrameLog("TX" + context, reply);
                        }
                    }
                }
            }
            catch (IOException ex) { if (running) Log("TCP/RTU " + peer + "：" + ex.Message); }
            catch (SocketException ex) { if (running) Log("TCP/RTU " + peer + "：" + ex.Message); }
            catch (ObjectDisposedException) { }
            finally
            {
                client.Close();
                lock (gate) { clients.Remove(client); tcpTargets.Remove(client); Monitor.PulseAll(gate); }
                Log("断开 TCP/RTU " + peer);
            }
        }
        public void StartSerial(string name, int baud, Parity parity, StopBits stopBits, bool mbap)
        {
            serial = new SerialPort(name, baud, parity, 8, stopBits);
            serial.ReadTimeout = 30;
            serial.WriteTimeout = 1000;
            serial.Handshake = Handshake.None;
            serial.Open();
            lock (gate) serialTarget = new RawTarget { Port = serial, Name = "串口 [" + name + "] #" + (++nextRawTarget) };
            running = true;
            new Thread(delegate() { SerialLoop(mbap); }) { IsBackground = true }.Start();
            Log("已打开 " + name + " / " + baud + " / 8 " + parity + " " + stopBits + (mbap ? "，MBAP 帧（串口扩展）" : "，Modbus RTU"));
        }
        /// <summary>打开标准 Modbus ASCII 串口，支持标准 7 位和兼容设备常见的 8 位配置。</summary>
        public void StartAscii(string name, int baud, Parity parity, StopBits stopBits, int dataBits)
        {
            if (dataBits != 7 && dataBits != 8) throw new ArgumentOutOfRangeException("dataBits", "ASCII 数据位必须是 7 或 8。");
            serial = new SerialPort(name, baud, parity, dataBits, stopBits);
            serial.ReadTimeout = 30;
            serial.WriteTimeout = 1000;
            serial.Handshake = Handshake.None;
            serial.Open();
            lock (gate) serialTarget = new RawTarget { Port = serial, Name = "串口 [" + name + "] #" + (++nextRawTarget) };
            running = true;
            new Thread(AsciiSerialLoop) { IsBackground = true }.Start();
            Log("已打开 " + name + " / " + baud + " / " + dataBits + " " + parity + " " + stopBits + "，Modbus ASCII");
        }
        private void AsciiSerialLoop()
        {
            AsciiFrameParser parser = new AsciiFrameParser();
            Stopwatch clock = Stopwatch.StartNew();
            while (running)
            {
                try
                {
                    int next = serial.ReadByte();
                    if (next < 0) continue;
                    byte[] frame = parser.Feed((byte)next, clock.ElapsedMilliseconds);
                    if (frame == null) continue;
                    byte[] reply = ProcessAscii(frame);
                    if (reply != null && WriteSerial(reply))
                    {
                        // 只有驱动成功接受 Write 后记录 TX；不把处理函数返回值视为已发送。
                        AsciiFrameLog("TX", reply);
                    }
                }
                catch (TimeoutException) { parser.Expire(clock.ElapsedMilliseconds); }
                catch (Exception ex)
                {
                    if (running) Log("串口已停止：" + ex.Message);
                    running = false;
                    break;
                }
            }
        }
        /// <summary>离线处理完整标准 ASCII 帧；校验失败、不匹配站号及广播均不回复。</summary>
        public byte[] ProcessAscii(byte[] frame)
        {
            if (frame == null || frame.Length < 9 || frame.Length > AsciiProtocol.MaximumFrameLength) return null;
            AsciiFrameLog("RX", frame);
            byte address; byte[] pdu;
            if (!AsciiProtocol.TryDecode(frame, out address, out pdu)) { Log("ASCII 格式或 LRC 错误，忽略"); return null; }
            bool broadcast = address == 0;
            if (!broadcast && address != unit) return null;
            if (broadcast && pdu[0] != 5 && pdu[0] != 6 && pdu[0] != 15 && pdu[0] != 16) return null;
            byte[] response = Bank.Process(pdu);
            Interlocked.Increment(ref requests);
            if (broadcast) { Log("已处理广播写入，不回复"); return null; }
            return AsciiProtocol.Encode(unit, response);
        }
        private void SerialLoop(bool mbap)
        {
            List<byte> buffer = new List<byte>();
            while (running)
            {
                try
                {
                    int next = serial.ReadByte();
                    if (next < 0) continue;
                    buffer.Add((byte)next);
                    int expected = 0;
                    if (mbap && buffer.Count >= 6)
                    {
                        int length = (buffer[4] << 8) | buffer[5];
                        if (buffer[2] != 0 || buffer[3] != 0 || length < 2 || length > 254)
                        { buffer.Clear(); Log("串口 MBAP 头无效，已丢弃"); continue; }
                        expected = 6 + length;
                    }
                    else if (!mbap && buffer.Count >= 2)
                    {
                        int fc = buffer[1];
                        if (fc >= 1 && fc <= 6) expected = 8;
                        else if ((fc == 15 || fc == 16) && buffer.Count >= 7) expected = 9 + buffer[6];
                    }
                    if (expected > 0 && buffer.Count == expected)
                    { HandleSerial(buffer.ToArray(), mbap); buffer.Clear(); }
                    else if (buffer.Count > 260) { buffer.Clear(); Log("串口帧过长，已丢弃"); }
                }
                catch (TimeoutException)
                {
                    // 未支持的 RTU 功能码以接收空闲作为帧边界，CRC 正确时返回异常码。
                    if (buffer.Count != 0)
                    {
                        if (!mbap) HandleSerial(buffer.ToArray(), false);
                        else Log("串口 MBAP 不完整帧已丢弃");
                        buffer.Clear();
                    }
                }
                catch (Exception ex)
                {
                    if (running) Log("串口已停止：" + ex.Message);
                    running = false;
                    break;
                }
            }
        }
        private void HandleSerial(byte[] frame, bool mbap)
        {
            byte[] reply = mbap ? ProcessMbap(frame) : ProcessRtu(frame);
            if (reply != null && WriteSerial(reply))
            {
                FrameLog("TX", reply);
                if (mbap) Log("已写入串口 TID=" + RegisterBank.Word(reply, 0) + " FC=0x" + reply[7].ToString("X2") + "，驱动待发=" + serial.BytesToWrite + " 字节");
            }
        }
        public byte[] ProcessRtu(byte[] frame)
        { return ProcessRtuCore(frame, ""); }
        private byte[] ProcessRtuCore(byte[] frame, string context)
        {
            if (frame == null || frame.Length < 4 || frame.Length > 256) return null;
            FrameLog("RX" + context, frame);
            ushort crc = RegisterBank.Crc(frame, frame.Length - 2);
            if (frame[frame.Length - 2] != (byte)crc || frame[frame.Length - 1] != (byte)(crc >> 8))
            { Log("CRC 错误，忽略"); return null; }
            bool broadcast = frame[0] == 0;
            if (!broadcast && frame[0] != unit) return null;
            if (broadcast && frame[1] != 5 && frame[1] != 6 && frame[1] != 15 && frame[1] != 16) return null;
            byte[] pdu = new byte[frame.Length - 3];
            Buffer.BlockCopy(frame, 1, pdu, 0, pdu.Length);
            byte[] response = Bank.Process(pdu);
            Interlocked.Increment(ref requests);
            if (broadcast) { Log("已处理广播写入，不回复"); return null; }
            byte[] reply = new byte[response.Length + 3];
            reply[0] = unit;
            Buffer.BlockCopy(response, 0, reply, 1, response.Length);
            crc = RegisterBank.Crc(reply, reply.Length - 2);
            reply[reply.Length - 2] = (byte)crc;
            reply[reply.Length - 1] = (byte)(crc >> 8);
            return reply;
        }
        public void Dispose()
        {
            running = false;
            if (listener != null) listener.Stop();
            if (udp != null) udp.Close();
            TcpClient[] closing;
            lock (gate) { closing = clients.ToArray(); udpTargets.Clear(); serialTarget = null; }
            foreach (TcpClient client in closing) client.Close();
            if (serial != null) { serial.Close(); serial.Dispose(); }
            // 关闭 socket 解除阻塞接收，再有界等待接收线程/连接回调退出。
            if (networkThread != null && networkThread != Thread.CurrentThread) networkThread.Join(2000);
            Stopwatch wait = Stopwatch.StartNew();
            lock (gate) while (clients.Count != 0 && wait.ElapsedMilliseconds < 2000) Monitor.Wait(gate, 50);
        }
    }
}
