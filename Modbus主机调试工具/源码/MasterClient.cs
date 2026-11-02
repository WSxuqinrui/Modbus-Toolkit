using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ModbusMaster
{
    /// <summary>稳定模式编号：0 TCP MBAP；1 串口 RTU；2 串口 MBAP；3 ASCII；4 UDP MBAP；5 RTU/TCP；6 RTU/UDP。</summary>
    public sealed class MasterConnectionOptions
    {
        public int Mode { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Com { get; set; }
        public int Baud { get; set; }
        public string Parity { get; set; }
        public string StopBits { get; set; }
        public int DataBits { get; set; }
        public int TimeoutMs { get; set; }
        public int Unit { get; set; }
        public MasterConnectionOptions()
        { Mode = 0; Host = "127.0.0.1"; Port = 502; Com = ""; Baud = 9600; Parity = "None"; StopBits = "1"; DataBits = 8; TimeoutMs = 1500; Unit = 1; }
        public void Validate()
        {
            if (Mode < 0 || Mode > 6) throw new ArgumentOutOfRangeException("Mode");
            if (TimeoutMs < 100 || TimeoutMs > 60000) throw new ArgumentOutOfRangeException("TimeoutMs", "超时范围为 100～60000 ms。");
            if (Unit < 1 || Unit > 247) throw new ArgumentOutOfRangeException("Unit", "站号范围为 1～247。");
            // 网络配置也会保存这些字段，必须与界面可表示的选项完全一致。
            if (Array.IndexOf(new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }, Baud) < 0) throw new ArgumentOutOfRangeException("Baud");
            if (Parity != "None" && Parity != "Odd" && Parity != "Even") throw new ArgumentException("校验位无效。", "Parity");
            if (StopBits != "1" && StopBits != "2") throw new ArgumentException("停止位无效。", "StopBits");
            if (DataBits != 8 && !(Mode == 3 && DataBits == 7)) throw new ArgumentOutOfRangeException("DataBits", "仅 ASCII 模式允许 7 位数据位，其余模式必须为 8 位。");
            if (Mode == 0 || Mode >= 4)
            {
                IPAddress address;
                if (!IPAddress.TryParse(Host, out address)) throw new ArgumentException("目标必须为 IPv4 或 IPv6 地址。", "Host");
                if (Port < 1 || Port > 65535) throw new ArgumentOutOfRangeException("Port");
            }
            else
            {
                SerialDevices.RawPort(Com);
            }
        }
        internal MasterConnectionOptions Copy()
        {
            return new MasterConnectionOptions { Mode = Mode, Host = Host, Port = Port, Com = Com, Baud = Baud, Parity = Parity, StopBits = StopBits, DataBits = DataBits, TimeoutMs = TimeoutMs, Unit = Unit };
        }
    }

    public interface IMasterConnection : IDisposable
    {
        bool IsConnected { get; }
        void Connect();
        byte[] Execute(byte unit, byte[] pdu);
    }

    /// <summary>直接传输调用方已完成的原始帧；不添加 MBAP、CRC、LRC 或换行。</summary>
    public interface IRawMasterConnection
    {
        byte[] ExchangeRaw(byte[] bytes, int receiveWindowMs);
    }

    /// <summary>
    /// 同一连接只允许一个在途事务。所有接收共用单调时钟总期限；不自动重试任何请求。
    /// Dispose 只使用短生命周期锁，不等待事务锁，可从 UI 线程关闭阻塞中的 IO。
    /// </summary>
    public sealed class ModbusMasterClient : IMasterConnection, IRawMasterConnection
    {
        private readonly MasterConnectionOptions options;
        private readonly Action<string> log;
        private readonly object transactionGate = new object(), lifetimeGate = new object();
        private readonly ManualResetEventSlim disposeSignal = new ManualResetEventSlim(false);
        private readonly MasterRtuSilentInterval rtuSilence;
        private Socket socket;
        private SerialPort serial;
        private bool connected, disposed;
        private ushort nextTransaction;
        public ModbusMasterClient(MasterConnectionOptions options, Action<string> log)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Validate(); this.options = options.Copy(); this.log = log;
            // RTU over TCP/UDP 不使用串口物理层 t3.5，只有稳定模式编号 1 启用。
            if (options.Mode == 1) rtuSilence = new MasterRtuSilentInterval(this.options, Stopwatch.Frequency);
        }
        public bool IsConnected { get { lock (lifetimeGate) return connected && !disposed; } }

        public void Connect()
        {
            lock (transactionGate)
            {
                lock (lifetimeGate) { ThrowIfDisposed(); if (connected) return; }
                try
                {
                    if (options.Mode == 0 || options.Mode >= 4) ConnectNetwork(); else ConnectSerial();
                    lock (lifetimeGate) { ThrowIfDisposed(); connected = true; }
                    Log("已连接 " + Peer);
                }
                catch { CloseResources(); throw; }
            }
        }
        private void ConnectNetwork()
        {
            IPAddress address = IPAddress.Parse(options.Host);
            bool udp = options.Mode == 4 || options.Mode == 6;
            Socket created = new Socket(address.AddressFamily, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);
            lock (lifetimeGate) { if (disposed) { created.Close(); ThrowIfDisposed(); } socket = created; }
            if (!udp) created.NoDelay = true;
            IAsyncResult pending = created.BeginConnect(new IPEndPoint(address, options.Port), null, null);
            using (WaitHandle completion = pending.AsyncWaitHandle)
            {
                if (!completion.WaitOne(options.TimeoutMs)) throw new TimeoutException("连接超时。");
                created.EndConnect(pending);
            }
        }
        private void ConnectSerial()
        {
            SerialPort created = new SerialPort(SerialDevices.RawPort(options.Com), options.Baud,
                (Parity)Enum.Parse(typeof(Parity), options.Parity), options.DataBits,
                options.StopBits == "1" ? StopBits.One : StopBits.Two);
            created.ReadTimeout = options.TimeoutMs; created.WriteTimeout = options.TimeoutMs;
            created.Handshake = Handshake.None;
            lock (lifetimeGate) { ThrowIfDisposed(); serial = created; }
            // 某些 USB 驱动的 Open 不遵守 SerialPort 超时；后台打开保证调用线程有限等待。
            Exception failure = null;
            ManualResetEvent done = new ManualResetEvent(false);
            Thread worker = new Thread(delegate()
            {
                try { created.Open(); created.DiscardInBuffer(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
                bool keep;
                lock (lifetimeGate) keep = !disposed && Object.ReferenceEquals(serial, created);
                if (!keep) { try { created.Dispose(); } catch { } }
            });
            worker.IsBackground = true; worker.Start();
            if (!done.WaitOne(options.TimeoutMs))
            {
                // 等待器随 worker 结束后释放，避免 Set 与 Dispose 的竞争。
                ThreadPool.QueueUserWorkItem(delegate { done.WaitOne(); done.Dispose(); });
                throw new TimeoutException("打开串口超时。");
            }
            done.Dispose();
            if (failure != null) throw new IOException("打开串口失败：" + failure.Message, failure);
            // 初次发送也先观察一段静默，防止在打开串口的中途接入正在结束的帧。
            if (rtuSilence != null) rtuSilence.RecordReceive(Stopwatch.GetTimestamp());
        }

        public byte[] Execute(byte unit, byte[] pdu)
        {
            if (unit < 1 || unit > 247) throw new ArgumentOutOfRangeException("unit");
            if (pdu == null) throw new ArgumentNullException("pdu");
            if (pdu.Length < 1 || pdu.Length > 253 || !SupportedFunction(pdu[0])) throw new ArgumentException("PDU 长度或功能码无效。", "pdu");
            byte[] request = (byte[])pdu.Clone();
            lock (transactionGate)
            {
                lock (lifetimeGate) { ThrowIfDisposed(); if (!connected) throw new InvalidOperationException("尚未连接。"); }
                Stopwatch timer = Stopwatch.StartNew();
                ushort tid = unchecked(++nextTransaction);
                bool mbap = options.Mode == 0 || options.Mode == 2 || options.Mode == 4;
                bool datagram = options.Mode == 4 || options.Mode == 6;
                byte[] frame = mbap ? EncodeMbap(tid, unit, request) : options.Mode == 3 ? AsciiProtocol.Encode(unit, request) : EncodeRtu(unit, request);
                try
                {
                    Send(frame, timer);
                    LogFrame("TX", frame, mbap ? (int?)tid : null);
                    if (datagram) return ReceiveDatagram(unit, request[0], tid, mbap, timer);
                    if (mbap) return ReceiveMbap(unit, request[0], tid, timer);
                    if (options.Mode == 3) return ReceiveAscii(unit, request[0], timer);
                    return ReceiveRtu(unit, request[0], timer);
                }
                catch (Exception ex)
                {
                    CloseResources();
                    if (ex is SocketException && ((SocketException)ex).SocketErrorCode == SocketError.TimedOut)
                        throw new TimeoutException("等待设备响应超时，连接已断开。", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// 原始发送与协议事务共用事务锁。无回包是正常结果；从首字节起静默 50ms 可提前结束，
        /// 持续有数据时仍以调用方指定的总接收窗口为上限，绝不把收到的字节解释为寄存器值。
        /// </summary>
        public byte[] ExchangeRaw(byte[] bytes, int receiveWindowMs)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (bytes.Length < 1 || bytes.Length > 8192) throw new ArgumentOutOfRangeException("bytes", "原始发送长度必须为 1～8192 字节。");
            if (receiveWindowMs < 100 || receiveWindowMs > 5000) throw new ArgumentOutOfRangeException("receiveWindowMs", "接收窗口必须为 100～5000 ms。");
            byte[] request = (byte[])bytes.Clone();
            lock (transactionGate)
            {
                lock (lifetimeGate) { ThrowIfDisposed(); if (!connected) throw new InvalidOperationException("尚未连接。"); }
                try
                {
                    // 复用 Send 保留串口 RTU 的 t3.5 静默、发送超时和 Dispose 中断机制。
                    Send(request, Stopwatch.StartNew());
                    LogRawFrame("TX", request);
                    return ReceiveRaw(receiveWindowMs);
                }
                catch
                {
                    // ReceiveRaw 的正常窗口结束不抛异常；真实 IO 错误仍关闭当前通道。
                    CloseResources(); throw;
                }
            }
        }

        private byte[] ReceiveRaw(int receiveWindowMs)
        {
            Stopwatch timer = Stopwatch.StartNew(); long lastReceived = -1;
            bool datagram = options.Mode == 4 || options.Mode == 6;
            byte[] buffer = new byte[datagram ? 65536 : 8192];
            using (MemoryStream received = new MemoryStream())
            {
                while (true)
                {
                    lock (lifetimeGate) ThrowIfDisposed();
                    long remaining = receiveWindowMs - timer.ElapsedMilliseconds;
                    if (lastReceived >= 0) remaining = Math.Min(remaining, 50 - (timer.ElapsedMilliseconds - lastReceived));
                    if (remaining <= 0) return received.ToArray();
                    int waitMs = (int)Math.Min(20L, remaining), count;
                    SerialPort port; lock (lifetimeGate) port = serial;
                    if (port != null)
                    {
                        int available = port.BytesToRead;
                        if (available == 0)
                        {
                            if (disposeSignal.Wait(waitMs)) throw new ObjectDisposedException("ModbusMasterClient");
                            continue;
                        }
                        // 只读取当前已缓冲数据，不以设备的协议超时延长原始接收窗口。
                        port.ReadTimeout = Math.Max(1, (int)remaining);
                        count = port.Read(buffer, 0, Math.Min(available, buffer.Length));
                        if (count <= 0) throw new EndOfStreamException("串口接收已关闭。");
                        if (rtuSilence != null) rtuSilence.RecordReceive(Stopwatch.GetTimestamp());
                    }
                    else
                    {
                        Socket current = CurrentSocket();
                        if (!current.Poll(waitMs * 1000, SelectMode.SelectRead)) continue;
                        current.ReceiveTimeout = Math.Max(1, (int)remaining);
                        count = current.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        if (count == 0 && !datagram) throw new EndOfStreamException("设备已关闭连接。");
                    }
                    if (count == 0) continue; // UDP 空数据报没有可返回字节，也不是断线。
                    byte[] segment = Slice(buffer, 0, count);
                    LogRawFrame("RX", segment);
                    received.Write(segment, 0, segment.Length); lastReceived = timer.ElapsedMilliseconds;
                }
            }
        }
        private byte[] ReceiveMbap(byte unit, byte function, ushort tid, Stopwatch timer)
        {
            while (true)
            {
                byte[] header = ReadExact(6, timer);
                int length = (header[4] << 8) | header[5];
                if (length < 2 || length > 254) { LogFrame("RX", header, (header[0] << 8) | header[1]); throw new IOException("MBAP Length 非法，无法可靠确定下一帧边界。"); }
                byte[] body = ReadExact(length, timer);
                byte[] raw = new byte[6 + length]; Buffer.BlockCopy(header, 0, raw, 0, 6); Buffer.BlockCopy(body, 0, raw, 6, length);
                int receivedTid = (header[0] << 8) | header[1]; LogFrame("RX", raw, receivedTid);
                byte[] result = Slice(body, 1, body.Length - 1);
                if (receivedTid == tid && header[2] == 0 && header[3] == 0 && body[0] == unit && Matches(function, result)) return result;
                Remaining(timer);
            }
        }
        private byte[] ReceiveDatagram(byte unit, byte function, ushort tid, bool mbap, Stopwatch timer)
        {
            byte[] buffer = new byte[65536];
            while (true)
            {
                Socket current = CurrentSocket(); current.ReceiveTimeout = Remaining(timer);
                int count = current.Receive(buffer);
                byte[] frame = Slice(buffer, 0, count); LogFrame("RX", frame, mbap && count >= 2 ? (int?)((frame[0] << 8) | frame[1]) : null);
                byte[] result = null;
                if (mbap)
                {
                    if (count >= 8 && count <= 260 && frame[0] == (byte)(tid >> 8) && frame[1] == (byte)tid && frame[2] == 0 && frame[3] == 0 &&
                        ((frame[4] << 8) | frame[5]) == count - 6 && frame[6] == unit) result = Slice(frame, 7, count - 7);
                }
                else
                {
                    byte address;
                    if (TryDecodeRtu(frame, out address, out result) && address != unit) result = null;
                }
                if (result != null && Matches(function, result)) return result;
                Remaining(timer);
            }
        }
        private byte[] ReceiveRtu(byte unit, byte function, Stopwatch timer)
        {
            MasterRtuFrameParser parser = new MasterRtuFrameParser();
            while (true)
            {
                byte[] frame = parser.Feed(ReadExact(1, timer)[0]);
                if (frame == null) continue;
                LogFrame("RX", frame, null);
                byte[] result; byte address;
                if (TryDecodeRtu(frame, out address, out result) && address == unit && Matches(function, result)) return result;
            }
        }
        private byte[] ReceiveAscii(byte unit, byte function, Stopwatch timer)
        {
            AsciiFrameParser parser = new AsciiFrameParser();
            while (true)
            {
                byte[] frame = parser.Feed(ReadExact(1, timer)[0], timer.ElapsedMilliseconds);
                if (frame == null) continue;
                LogFrame("RX", frame, null);
                byte[] result; byte address;
                if (AsciiProtocol.TryDecode(frame, out address, out result) && address == unit && Matches(function, result)) return result;
            }
        }
        private void Send(byte[] bytes, Stopwatch timer)
        {
            SerialPort port; lock (lifetimeGate) port = serial;
            if (port != null)
            {
                if (rtuSilence != null) WaitForSerialRtuQuiet(port, timer);
                port.WriteTimeout = Remaining(timer); port.Write(bytes, 0, bytes.Length);
                // Write 只保证交给驱动，不代表最后停止位已经发出。用排队完成时刻加串行化
                // 时间作保守排空估计；后续实际 RX 会取代估计，以最后收到的字节为准。
                if (rtuSilence != null) rtuSilence.RecordTransmitQueued(Stopwatch.GetTimestamp(), bytes.Length);
                return;
            }
            Socket current = CurrentSocket();
            int sent = 0;
            do
            {
                current.SendTimeout = Remaining(timer);
                int count = current.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                if (count <= 0) throw new IOException("发送时连接已关闭。");
                sent += count;
                if ((options.Mode == 4 || options.Mode == 6) && sent != bytes.Length) throw new IOException("UDP 帧未完整发送。");
            } while (sent < bytes.Length);
        }
        private void WaitForSerialRtuQuiet(SerialPort port, Stopwatch timer)
        {
            rtuSilence.Wait(Stopwatch.GetTimestamp, delegate { return Remaining(timer); }, disposeSignal.Wait, delegate
            {
                // 静默等待时也检查迟到的字节。每个收到的字节都重启 t3.5；旧帧不参与新事务。
                // 只缓存一小段日志，噪声持续存在时仍由同一个事务 deadline 终止等待。
                List<byte> pending = new List<byte>(256); byte[] one = new byte[1];
                while (port.BytesToRead > 0)
                {
                    port.ReadTimeout = Remaining(timer);
                    if (port.Read(one, 0, 1) <= 0) throw new EndOfStreamException("串口接收已关闭。");
                    rtuSilence.RecordReceive(Stopwatch.GetTimestamp()); pending.Add(one[0]);
                    if (pending.Count == 256) { LogFrame("RX", pending.ToArray(), null); pending.Clear(); }
                }
                if (pending.Count != 0) LogFrame("RX", pending.ToArray(), null);
            });
        }
        private byte[] ReadExact(int length, Stopwatch timer)
        {
            byte[] buffer = new byte[length]; int read = 0;
            while (read < length)
            {
                int count;
                SerialPort port; lock (lifetimeGate) port = serial;
                if (port != null)
                {
                    port.ReadTimeout = Remaining(timer); count = port.Read(buffer, read, length - read);
                    // Mode1 的 RTU 接收每次只读一字节，CRC 错帧及错站号也算总线活动。
                    if (count > 0 && rtuSilence != null) rtuSilence.RecordReceive(Stopwatch.GetTimestamp());
                }
                else { Socket current = CurrentSocket(); current.ReceiveTimeout = Remaining(timer); count = current.Receive(buffer, read, length - read, SocketFlags.None); }
                if (count <= 0) throw new EndOfStreamException("设备已关闭连接，响应帧不完整。");
                read += count;
            }
            return buffer;
        }
        private int Remaining(Stopwatch timer)
        {
            long remaining = options.TimeoutMs - timer.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException("等待匹配响应超时，连接已断开。");
            return (int)remaining;
        }
        private Socket CurrentSocket()
        { lock (lifetimeGate) { ThrowIfDisposed(); if (socket == null) throw new IOException("连接已关闭。"); return socket; } }
        private string Peer { get { return options.Mode == 0 || options.Mode >= 4 ? new IPEndPoint(IPAddress.Parse(options.Host), options.Port).ToString() : SerialDevices.RawPort(options.Com); } }
        private void Log(string text) { if (log != null) { try { log(text); } catch { } } }
        private void LogFrame(string direction, byte[] bytes, int? tid)
        { Log(direction + ": " + Peer + (tid.HasValue ? " TID=" + tid.Value : "") + " " + BitConverter.ToString(bytes).Replace('-', ' ')); }
        private void LogRawFrame(string direction, byte[] bytes)
        { Log(direction + ": RAW " + Peer + " " + BitConverter.ToString(bytes).Replace('-', ' ')); }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("ModbusMasterClient"); }
        private void CloseResources()
        {
            Socket oldSocket; SerialPort oldSerial;
            lock (lifetimeGate) { connected = false; oldSocket = socket; oldSerial = serial; socket = null; serial = null; }
            if (oldSocket != null) { try { oldSocket.Close(); } catch { } }
            // 避免 USB 驱动 Close 阻塞 UI；关闭仍会中断该串口上的读取。
            if (oldSerial != null) ThreadPool.QueueUserWorkItem(delegate { try { oldSerial.Dispose(); } catch { } });
        }
        public void Dispose() { disposeSignal.Set(); lock (lifetimeGate) disposed = true; CloseResources(); }

        private static bool SupportedFunction(byte fc) { return fc == 1 || fc == 2 || fc == 3 || fc == 4 || fc == 5 || fc == 6 || fc == 15 || fc == 16; }
        internal static bool Matches(byte function, byte[] pdu)
        {
            if (pdu == null || pdu.Length == 0) return false;
            if (pdu[0] == (byte)(function | 0x80)) return pdu.Length == 2;
            if (pdu[0] != function) return false;
            if (function <= 4) return pdu.Length >= 3 && pdu[1] >= 1 && pdu[1] <= 250 && pdu.Length == pdu[1] + 2;
            return pdu.Length == 5;
        }
        internal static byte[] EncodeMbap(ushort tid, byte unit, byte[] pdu)
        {
            byte[] frame = new byte[pdu.Length + 7]; frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
            frame[4] = (byte)((pdu.Length + 1) >> 8); frame[5] = (byte)(pdu.Length + 1); frame[6] = unit;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length); return frame;
        }
        internal static byte[] EncodeRtu(byte unit, byte[] pdu)
        {
            byte[] frame = new byte[pdu.Length + 3]; frame[0] = unit; Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);
            ushort crc = RegisterBank.Crc(frame, frame.Length - 2); frame[frame.Length - 2] = (byte)crc; frame[frame.Length - 1] = (byte)(crc >> 8); return frame;
        }
        internal static bool TryDecodeRtu(byte[] frame, out byte unit, out byte[] pdu)
        {
            unit = 0; pdu = null;
            if (frame == null || frame.Length < 5 || frame.Length > 256) return false;
            ushort crc = RegisterBank.Crc(frame, frame.Length - 2);
            if (frame[frame.Length - 2] != (byte)crc || frame[frame.Length - 1] != (byte)(crc >> 8)) return false;
            unit = frame[0]; pdu = Slice(frame, 1, frame.Length - 3); return true;
        }
        private static byte[] Slice(byte[] data, int offset, int count)
        { byte[] result = new byte[count]; Buffer.BlockCopy(data, offset, result, 0, count); return result; }
    }

    /// <summary>
    /// 串口 RTU 物理层静默计算器，使用调用者的单调时钟频率。
    /// 依据 Modbus Serial Line §2.5.1.1：≤19200 波特为实际字符的 3.5 倍，
    /// 更高波特率固定至少 1.75 ms。所有 tick 都向上取整，不能提前发送。
    /// </summary>
    internal sealed class MasterRtuSilentInterval
    {
        private readonly long frequency, quietTicks;
        private readonly int baud, bitsPerCharacter;
        private long lastActivityEnd;
        private bool hasActivity;
        public MasterRtuSilentInterval(MasterConnectionOptions options, long clockFrequency)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (clockFrequency <= 0) throw new ArgumentOutOfRangeException("clockFrequency");
            frequency = clockFrequency; baud = options.Baud;
            bitsPerCharacter = 1 + options.DataBits + (options.Parity == "None" ? 0 : 1) + (options.StopBits == "2" ? 2 : 1);
            quietTicks = (long)Math.Ceiling(frequency * (baud > 19200 ? 0.00175 : 3.5 * bitsPerCharacter / baud));
        }
        public void RecordReceive(long timestamp)
        {
            // 已经收到从机响应意味着真实发送已经结束，不能保留早先的保守 TX 排空估计。
            lastActivityEnd = timestamp; hasActivity = true;
        }
        public void RecordTransmitQueued(long timestamp, int byteCount)
        {
            if (byteCount < 0) throw new ArgumentOutOfRangeException("byteCount");
            long serializationTicks = (long)Math.Ceiling((double)frequency * bitsPerCharacter * byteCount / baud);
            lastActivityEnd = timestamp + serializationTicks; hasActivity = true;
        }
        public long RemainingTicks(long timestamp)
        { return hasActivity ? Math.Max(0L, lastActivityEnd + quietTicks - timestamp) : 0L; }
        public void Wait(Func<long> now, Func<int> remainingDeadlineMs, Func<int, bool> waitCancelled, Action pollActivity)
        {
            while (true)
            {
                remainingDeadlineMs();
                if (pollActivity != null) pollActivity();
                int deadlineMs = remainingDeadlineMs();
                long pending = RemainingTicks(now());
                if (pending == 0) return;
                // 毫秒等待向上取整，醒来后重新看单调时钟。调度晚醒也不能刷新总期限。
                int quietMs = (int)Math.Min(Int32.MaxValue, Math.Ceiling((double)pending * 1000 / frequency));
                if (waitCancelled(Math.Min(quietMs, deadlineMs))) throw new ObjectDisposedException("ModbusMasterClient");
            }
        }
    }

    /// <summary>最多保留 512 字节；扫描所有候选起点，坏 CRC/伪长帧不会阻塞后续合法帧。</summary>
    internal sealed class MasterRtuFrameParser
    {
        private readonly List<byte> bytes = new List<byte>(512);
        public byte[] Feed(byte value)
        {
            if (bytes.Count >= 512) bytes.RemoveAt(0);
            bytes.Add(value);
            for (int start = 0; start + 4 < bytes.Count; start++)
            {
                byte fc = bytes[start + 1]; int length;
                if ((fc & 0x80) != 0 && (fc & 0x7F) >= 1 && (fc & 0x7F) <= 16) length = 5;
                else if (fc >= 1 && fc <= 4) { int count = bytes[start + 2]; if (count < 1 || count > 250) continue; length = count + 5; }
                else if (fc == 5 || fc == 6 || fc == 15 || fc == 16) length = 8;
                else continue;
                if (start + length > bytes.Count) continue;
                byte[] candidate = bytes.GetRange(start, length).ToArray(); byte unit; byte[] pdu;
                if (!ModbusMasterClient.TryDecodeRtu(candidate, out unit, out pdu)) continue;
                bytes.RemoveRange(0, start + length); return candidate;
            }
            return null;
        }
    }
}
