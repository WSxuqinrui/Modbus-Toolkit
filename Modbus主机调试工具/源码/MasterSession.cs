using System;
using System.Collections.Generic;
using System.Threading;

namespace ModbusMaster
{
    /// <summary>
    /// 单工作线程串行化连接、读写和轮询；UI调用仅入队，不等待设备响应。
    /// 缓存只接受经过协议校验的读响应，写确认不能作为设备实时值。
    /// </summary>
    public sealed class MasterSession : IDisposable
    {
        private const int QueueLimit = 128;
        private readonly object gate = new object();
        private readonly RegisterBank cache;
        private readonly IMasterConnection connection;
        private readonly byte unit;
        private readonly Action<string> log;
        private readonly Queue<WorkItem> queue = new Queue<WorkItem>();
        private readonly int[] starts = new int[4], counts = new int[4];
        private readonly DateTime?[] lastSuccess = new DateTime?[4];
        private readonly DateTime?[] lastPartialSuccess = new DateTime?[4];
        private readonly int[] partialAddresses = new int[4], partialCounts = new int[4];
        private readonly bool[] fresh = new bool[4], failed = new bool[4];
        private readonly string[] areaErrors = new string[4];
        private Thread worker;
        private bool disposed, busy, polling, running, rawPending;
        private const string RawStateText = "原始发送模式，已暂停轮询；重新连接恢复Modbus";
        private int pollIntervalMs = 500;
        private DateTime nextPollUtc;
        private string stateText = "未连接";
        private long requests, errors;

        private sealed class WorkItem
        {
            public bool IsRead;
            public int Area, Address;
            public ushort[] Values;
            public bool Multiple;
            public byte[] RawBytes;
            public int ReceiveWindowMs;
        }

        public MasterSession(RegisterBank cache, MasterConnectionOptions options, Action<string> log)
            : this(cache, options, log, CreateConnection(options, log)) { }

        /// <summary>可注入通道以进行不打开真实串口的调度/缓存测试；会话拥有并关闭该通道。</summary>
        public MasterSession(RegisterBank cache, MasterConnectionOptions options, Action<string> log, IMasterConnection connection)
        {
            if (cache == null) throw new ArgumentNullException("cache");
            if (options == null) throw new ArgumentNullException("options");
            if (connection == null) throw new ArgumentNullException("connection");
            options.Validate();
            this.cache = cache; this.connection = connection; this.log = log;
            unit = Convert.ToByte(options.Unit);
            // 连接期间数据区定义固定；UI可以继续调整解析及显示信息。
            for (int area = 0; area < 4; area++) { starts[area] = cache.Start(area); counts[area] = cache.Count(area); }
        }

        private static IMasterConnection CreateConnection(MasterConnectionOptions options, Action<string> log)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Validate(); return new ModbusMasterClient(options, log);
        }

        public bool IsConnected { get { lock (gate) return !disposed && connection.IsConnected; } }
        public bool IsBusy { get { lock (gate) return busy; } }
        public bool HasPendingWork { get { lock (gate) return busy || queue.Count != 0; } }
        public bool IsPolling { get { lock (gate) return polling; } }
        /// <summary>原始命令一入队即锁定类型化操作，包含尚未轮到发送的 pending 阶段。</summary>
        public bool IsRawMode { get { lock (gate) return rawPending; } }
        /// <summary>从Start设置到worker退出，连接中也为true；用于锁定通信参数与数据区定义。</summary>
        public bool IsRunning { get { lock (gate) return running; } }
        public string StateText { get { lock (gate) return stateText; } }
        public long Requests { get { return Interlocked.Read(ref requests); } }
        public long Errors { get { return Interlocked.Read(ref errors); } }

        private static void CheckArea(int area)
        { if (area < 0 || area > 3) throw new ArgumentOutOfRangeException("area"); }

        public bool AreaHasFreshData(int area)
        { CheckArea(area); lock (gate) return fresh[area]; }

        public string GetAreaStatus(int area)
        {
            CheckArea(area);
            lock (gate)
            {
                if (counts[area] == 0) return "未读取（空数据区）";
                string previous = lastSuccess[area].HasValue
                    ? "上次成功 " + lastSuccess[area].Value.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    : "尚无成功读取；本地缓存/配置快照";
                string status;
                if (failed[area]) status = "读取失败/上次值（" + previous + "）" + (String.IsNullOrEmpty(areaErrors[area]) ? "" : "：" + areaErrors[area]);
                else if (!fresh[area]) status = "整区未读取（本地缓存/配置快照）";
                else status = "整区读取成功（" + previous + "）";
                if (lastPartialSuccess[area].HasValue)
                    status += "；局部写回读成功 地址" + partialAddresses[area] + "～" +
                        (partialAddresses[area] + partialCounts[area] - 1) + "（" + lastPartialSuccess[area].Value.ToString("HH:mm:ss.fff") + "，整区状态不变）";
                return status;
            }
        }

        /// <summary>只启动后台线程，连接失败在StateText/日志中报告，不阻塞调用线程。</summary>
        public void Start()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (running) return;
                // 会话断线后不自动重连；显式新建Session才建立新的连接和新鲜度边界。
                if (worker != null) throw new InvalidOperationException("当前会话已经结束，请重新建立连接。");
                stateText = "连接中…"; busy = true; running = true;
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Modbus主机事务" };
                worker.Start();
            }
        }

        public void ReadOnce()
        {
            lock (gate)
            {
                EnsureConnected();
                EnsureTypedMode();
                // 重复点击读取可合并，显式写命令不合并也不丢弃。
                foreach (WorkItem pending in queue) if (pending.IsRead) return;
                Enqueue(new WorkItem { IsRead = true });
            }
        }

        public void SetPolling(bool enabled, int intervalMs)
        {
            if (intervalMs < 50 || intervalMs > 60000) throw new ArgumentOutOfRangeException("intervalMs", "轮询周期必须在50～60000毫秒之间。");
            lock (gate)
            {
                ThrowIfDisposed();
                if (enabled) { EnsureConnected(); EnsureTypedMode(); }
                pollIntervalMs = intervalMs;
                polling = enabled;
                nextPollUtc = DateTime.UtcNow;
                if (connection.IsConnected) stateText = rawPending ? RawStateText : enabled ? "已连接，轮询中" : "已连接";
                Monitor.PulseAll(gate);
            }
        }

        public void EnqueueWrite(int area, int address, ushort[] values, bool multiple)
        {
            if (area != 0 && area != 2) throw new ArgumentException("只允许写入线圈或保持寄存器。", "area");
            if (values == null || values.Length == 0) throw new ArgumentException("写入数据不能为空。", "values");
            if (!multiple && values.Length != 1) throw new ArgumentException("单写只能包含一个值。", "values");
            if (address < starts[area] || address - starts[area] >= counts[area] || values.Length > counts[area] - (address - starts[area]))
                throw new ArgumentOutOfRangeException("address", "写入地址超出当前定义的数据区。");
            ushort[] copy = (ushort[])values.Clone();
            if (area == 0) foreach (ushort value in copy) if (value > 1) throw new ArgumentException("线圈值只能是0或1。", "values");
            lock (gate)
            {
                EnsureConnected();
                EnsureTypedMode();
                Enqueue(new WorkItem { Area = area, Address = address, Values = copy, Multiple = multiple });
            }
        }

        /// <summary>
        /// 原始数据深拷贝入同一有界 FIFO。已有命令按原顺序完成；首条原始命令起停止生成轮询，
        /// 新的 Modbus 读写必须等用户断开重连，防止无事务号的迟到数据被解释为协议响应。
        /// </summary>
        public void EnqueueRaw(byte[] data, int receiveWindowMs)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length < 1 || data.Length > 8192) throw new ArgumentOutOfRangeException("data", "原始发送长度必须为 1～8192 字节。");
            if (receiveWindowMs < 100 || receiveWindowMs > 5000) throw new ArgumentOutOfRangeException("receiveWindowMs", "接收窗口必须为 100～5000 ms。");
            byte[] copy = (byte[])data.Clone();
            lock (gate)
            {
                EnsureConnected();
                if (!(connection is IRawMasterConnection)) throw new NotSupportedException("当前通道不支持原始发送。");
                // Enqueue 若因满队列失败，不改变模式和轮询状态，也不丢弃任何已授权命令。
                Enqueue(new WorkItem { RawBytes = copy, ReceiveWindowMs = receiveWindowMs });
                rawPending = true; polling = false; stateText = RawStateText;
            }
        }

        private void EnsureTypedMode()
        { if (rawPending) throw new InvalidOperationException("原始发送模式下已暂停 Modbus 读写和轮询，请断开并重新连接后恢复。"); }

        private void Enqueue(WorkItem item)
        {
            if (queue.Count >= QueueLimit) throw new InvalidOperationException("命令队列已满（128项），本次命令未入队，请稍后再试。");
            queue.Enqueue(item); Monitor.PulseAll(gate);
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("MasterSession"); }
        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!running || !connection.IsConnected) throw new InvalidOperationException("尚未连接设备。");
        }

        private void WorkerLoop()
        {
            try
            {
                try
                {
                    connection.Connect();
                    lock (gate)
                    {
                        if (disposed) return;
                        busy = false; stateText = "已连接";
                    }
                    SafeLog("连接成功。");
                }
                catch (Exception ex)
                {
                    lock (gate) { if (!disposed) { stateText = "连接失败：" + ex.Message; Interlocked.Increment(ref errors); } }
                    SafeLog("连接失败：" + ex.Message); return;
                }
                while (true)
                {
                    WorkItem item;
                    lock (gate)
                    {
                        while (true)
                        {
                            if (disposed || !connection.IsConnected) return;
                            if (queue.Count > 0) { item = queue.Dequeue(); break; }
                            if (polling)
                            {
                                int remaining = (int)Math.Ceiling((nextPollUtc - DateTime.UtcNow).TotalMilliseconds);
                                if (remaining <= 0) { item = new WorkItem { IsRead = true }; break; }
                                Monitor.Wait(gate, Math.Min(remaining, 60000));
                            }
                            else Monitor.Wait(gate);
                        }
                        busy = true;
                    }
                    try
                    {
                        if (item.RawBytes != null) ExchangeRaw(item);
                        else if (item.IsRead) ReadAll();
                        else WriteAndReadback(item);
                    }
                    catch (Exception ex)
                    {
                        if (!IsDisposed)
                        {
                            if (!item.IsRead && item.RawBytes == null) MarkFailed(item.Area, ex.Message);
                            lock (gate) stateText = connection.IsConnected ? (rawPending ? RawStateText + "；操作失败：" + ex.Message : "操作失败：" + ex.Message) : "通信已断开，轮询停止：" + ex.Message;
                            SafeLog("操作失败：" + ex.Message);
                        }
                    }
                    finally
                    {
                        lock (gate)
                        {
                            busy = false;
                            if (!disposed && rawPending && connection.IsConnected) stateText = RawStateText;
                            if (item.IsRead) nextPollUtc = DateTime.UtcNow.AddMilliseconds(pollIntervalMs);
                            if (!disposed && !connection.IsConnected)
                            {
                                polling = false;
                                if (!stateText.Contains("断")) stateText = "通信已断开，轮询停止";
                                CancelQueue();
                            }
                        }
                    }
                }
            }
            finally
            {
                try { connection.Dispose(); } catch (Exception ex) { SafeLog("关闭通道失败：" + ex.Message); }
                lock (gate)
                {
                    busy = false; polling = false; running = false; CancelQueue();
                    if (disposed) stateText = "已断开";
                    else if (stateText == "已连接" || stateText == "已连接，轮询中") stateText = "通信已断开，轮询停止";
                    Monitor.PulseAll(gate);
                }
            }
        }

        private bool IsDisposed { get { lock (gate) return disposed; } }
        private void ExchangeRaw(WorkItem item)
        {
            Interlocked.Increment(ref requests);
            try { ((IRawMasterConnection)connection).ExchangeRaw(item.RawBytes, item.ReceiveWindowMs); }
            catch { if (!IsDisposed) Interlocked.Increment(ref errors); throw; }
            // 返回值只属于日志/原始接收，不参与任何数据区的值、成功时间戳或新鲜度。
            lock (gate) if (!disposed) stateText = RawStateText;
        }
        private void ReadAll()
        {
            for (int area = 0; area < 4; area++)
            {
                if (counts[area] == 0 || IsDisposed) continue;
                try
                {
                    ReadRange(area, starts[area], counts[area]);
                    MarkSuccess(area);
                }
                catch (Exception ex)
                {
                    if (IsDisposed) return;
                    MarkFailed(area, ex.Message);
                    lock (gate) stateText = connection.IsConnected ? "读取失败，显示上次值：" + ex.Message : "通信已断开，轮询停止：" + ex.Message;
                    SafeLog("数据区" + area + "读取失败：" + ex.Message);
                    if (!connection.IsConnected) return;
                }
            }
        }

        private void ReadRange(int area, int address, int count)
        {
            int limit = area < 2 ? 2000 : 125;
            // 整个逻辑读取区间先暂存；跨125字帧边界的32/64位值不会出现一半新、一半旧。
            // 最大65536字为128 KB；任一分块失败或取消时直接丢弃暂存，不修改缓存。
            ushort[] staged = new ushort[count];
            for (int offset = 0; offset < count; )
            {
                if (IsDisposed) throw new OperationCanceledException("会话已取消。");
                int length = Math.Min(limit, count - offset);
                byte[] request = MasterProtocol.ReadRequest(area, address + offset, length);
                ushort[] values = ReadTransaction(request);
                Array.Copy(values, 0, staged, offset, length);
                offset += length;
            }
            // WriteWords在RegisterBank同一锁内一次提交全部字；取消后的响应不得刷新UI缓存。
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException("会话已取消。");
                cache.WriteWords(area, address, staged);
            }
        }

        private void WriteAndReadback(WorkItem item)
        {
            int limit = item.Area == 0 ? 1968 : 123;
            for (int offset = 0; offset < item.Values.Length; )
            {
                if (IsDisposed) return;
                int length = Math.Min(limit, item.Values.Length - offset);
                ushort[] block = new ushort[length]; Array.Copy(item.Values, offset, block, 0, length);
                byte[] request = item.Multiple
                    ? MasterProtocol.WriteMultiple(item.Area, item.Address + offset, block)
                    : MasterProtocol.WriteSingle(item.Area, item.Address + offset, block[0]);
                WriteTransaction(request);
                // 每块写ACK校验成功后立即回读同一区间，回读失败绝不提交写入请求值。
                ReadRange(item.Area, item.Address + offset, length);
                // 局部回读只证明本块有效，不能把其它地址的配置快照或失败旧值升级为整区新值。
                // 即使本次写覆盖全区，也保守等待下一次完整读取后才设置整区fresh。
                MarkPartialSuccess(item.Area, item.Address + offset, length);
                offset += length;
            }
        }

        private ushort[] ReadTransaction(byte[] request)
        {
            Interlocked.Increment(ref requests);
            try { return MasterProtocol.ParseRead(request, connection.Execute(unit, request)); }
            catch { if (!IsDisposed) Interlocked.Increment(ref errors); throw; }
        }
        private void WriteTransaction(byte[] request)
        {
            Interlocked.Increment(ref requests);
            try { MasterProtocol.ValidateWrite(request, connection.Execute(unit, request)); }
            catch { if (!IsDisposed) Interlocked.Increment(ref errors); throw; }
        }
        private void MarkSuccess(int area)
        {
            lock (gate)
            {
                if (disposed) return;
                fresh[area] = true; failed[area] = false; areaErrors[area] = null; lastSuccess[area] = DateTime.Now;
                lastPartialSuccess[area] = null;
                stateText = rawPending ? RawStateText : polling ? "已连接，轮询中" : "已连接";
            }
        }
        private void MarkPartialSuccess(int area, int address, int count)
        {
            lock (gate)
            {
                if (disposed) return;
                lastPartialSuccess[area] = DateTime.Now; partialAddresses[area] = address; partialCounts[area] = count;
                // 完整读取的fresh、失败原因及时间戳均保持原状。
                stateText = rawPending ? RawStateText : failed[area] ? "局部写入已回读，整区仍为读取失败/上次值"
                    : polling ? "已连接，轮询中" : "已连接，局部写入已回读";
            }
        }
        private void MarkFailed(int area, string message)
        { lock (gate) { fresh[area] = false; failed[area] = true; areaErrors[area] = message; lastPartialSuccess[area] = null; } }
        private void CancelQueue()
        {
            int count = queue.Count; queue.Clear();
            if (count > 0) SafeLog("已取消" + count + "条尚未执行的命令；未自动重试写入。");
        }
        private void SafeLog(string message)
        { if (log != null) try { log(message); } catch { /* 日志接收方异常不能终止通信线程。 */ } }

        public void Dispose()
        {
            Thread stopping;
            lock (gate)
            {
                if (disposed) return;
                disposed = true; polling = false; stateText = "正在断开…"; CancelQueue();
                stopping = worker; Monitor.PulseAll(gate);
            }
            // 必须在等待worker之前关闭IO，打断Connect/Read，避免UI被完整超时时间阻塞。
            try { connection.Dispose(); } catch (Exception ex) { SafeLog("关闭通道失败：" + ex.Message); }
            if (stopping != null && stopping != Thread.CurrentThread) stopping.Join(1500);
            lock (gate) { if (!running) { busy = false; stateText = "已断开"; } }
        }
    }
}
