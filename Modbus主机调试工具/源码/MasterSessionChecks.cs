using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ModbusMaster
{
    /// <summary>无硬件检查：假通道只替代传输，实际调度、PDU校验和缓存均使用产品代码。</summary>
    public static class MasterSessionChecks
    {
        public static int Run() { return Run(Console.WriteLine); }
        public static int Run(Action<string> report)
        {
            int passed = 0;
            Action<string, Action> check = delegate(string name, Action test)
            {
                test(); passed++;
                if (report != null) report("PASS session " + name);
            };
            check("连接在后台、初始缓存不是新读值", BackgroundConnect);
            check("四区读取分块并跳过空区", ReadChunksAndEmptyAreas);
            check("分块读取等待及失败不提交半区、成功一次提交", AtomicReadCommit);
            check("局部写回读不升级或覆盖整区读取状态", PartialWritePreservesAreaState);
            check("写确认不改缓存、只提交回读值", WriteReadback);
            check("回读异常保留旧值并计错、不重写", FailedReadback);
            check("普通读取异常保留旧值、后续区仍可读取", FailedAreaRead);
            check("写分块和单/多写功能码", WriteChunks);
            check("队列深拷贝、有界且拒绝无效写入", QueueValidation);
            check("断线停止轮询、Dispose中断与取消", DisconnectAndCancel);
            check("配置保存完整且校验失败不破坏旧文件", ConfigurationRoundtrip);
            return passed;
        }

        private static RegisterBank Bank(int coils, int discrete, int holding, int input)
        {
            RegisterBank bank = new RegisterBank();
            int[] counts = { coils, discrete, holding, input };
            for (int area = 0; area < 4; area++) bank.Configure(area, 10, counts[area]);
            return bank;
        }
        private static MasterConnectionOptions Options() { return new MasterConnectionOptions(); }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("会话检查失败：" + message); }
        private static void Wait(Func<bool> condition, string message)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > 4000) throw new TimeoutException("会话检查等待超时：" + message);
                Thread.Sleep(5);
            }
        }
        private static void Reject(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("会话检查应拒绝：" + message);
        }
        private static void Connected(MasterSession session)
        { session.Start(); Wait(delegate { return session.IsConnected && !session.IsBusy; }, "后台连接完成"); }

        private static void BackgroundConnect()
        {
            FakeConnection channel = new FakeConnection(Bank(0, 0, 1, 0));
            channel.ConnectRelease.Reset();
            using (MasterSession session = new MasterSession(Bank(0, 0, 1, 0), Options(), null, channel))
            {
                Assert(!session.AreaHasFreshData(2) && session.GetAreaStatus(2).Contains("未读取"), "配置值必须标注未读取");
                Stopwatch watch = Stopwatch.StartNew(); session.Start();
                Assert(watch.ElapsedMilliseconds < 500, "Start不应等待连接");
                Wait(delegate { return channel.ConnectEntered.WaitOne(0); }, "进入后台连接");
                Assert(session.IsRunning && !session.IsConnected, "Connecting阶段worker应存活");
                channel.ConnectRelease.Set();
                Wait(delegate { return session.IsConnected; }, "连接完成");
            }
            FakeConnection failing = new FakeConnection(Bank(0, 0, 1, 0));
            failing.ConnectFailure = new IOException("测试连接失败");
            using (MasterSession session = new MasterSession(Bank(0, 0, 1, 0), Options(), null, failing))
            {
                session.Start(); Wait(delegate { return !session.IsRunning; }, "连接失败退出");
                Assert(!session.IsConnected && session.StateText.Contains("失败"), "连接失败要有明确状态");
            }
        }

        private static void ReadChunksAndEmptyAreas()
        {
            RegisterBank remote = Bank(2001, 0, 126, 0), cache = Bank(2001, 0, 126, 0);
            remote.Set(0, 2010, 1); remote.Set(2, 135, 4567);
            FakeConnection channel = new FakeConnection(remote);
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Connected(session); session.ReadOnce();
                Wait(delegate { return session.AreaHasFreshData(2) && !session.IsBusy; }, "分块读取完成");
                byte[][] requests = channel.Requests();
                Assert(requests.Length == 4 && session.Requests == 4 && session.Errors == 0, "仅应读取四个合法分块");
                Assert(requests[0][0] == 1 && RegisterBank.Word(requests[0], 3) == 2000 && RegisterBank.Word(requests[1], 3) == 1, "位读限制2000");
                Assert(requests[2][0] == 3 && RegisterBank.Word(requests[2], 3) == 125 && RegisterBank.Word(requests[3], 3) == 1, "字读限制125");
                Assert(cache.Get(0, 2010) == 1 && cache.Get(2, 135) == 4567, "完整尾块必须写缓存");
                Assert(!session.AreaHasFreshData(1) && session.GetAreaStatus(2).Contains("成功"), "空区不伪造读取时间");
            }
        }

        private static void WriteReadback()
        {
            RegisterBank remote = Bank(0, 0, 1, 0), cache = Bank(0, 0, 1, 0);
            cache.Set(2, 10, 9);
            FakeConnection channel = new FakeConnection(remote);
            ManualResetEvent reading = new ManualResetEvent(false), release = new ManualResetEvent(false);
            channel.Handler = delegate(byte[] request)
            {
                if (request[0] == 6) { byte[] ack = remote.Process(request); remote.Set(2, 10, 81); return ack; }
                reading.Set(); release.WaitOne(); return remote.Process(request);
            };
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Connected(session); session.EnqueueWrite(2, 10, new ushort[] { 77 }, false);
                Wait(delegate { return reading.WaitOne(0); }, "写后回读开始");
                Assert(cache.Get(2, 10) == 9 && !session.AreaHasFreshData(2), "写ACK不得把请求值冒充读回值");
                release.Set(); Wait(delegate { return session.Requests == 2 && !session.IsBusy; }, "回读完成");
                Assert(cache.Get(2, 10) == 81 && session.Requests == 2, "缓存应取设备实际回读值");
            }
            reading.Dispose(); release.Dispose();
        }

        private static void AtomicReadCommit()
        {
            CheckAtomicReadCommit(false);
            CheckAtomicReadCommit(true);
        }

        private static void CheckAtomicReadCommit(bool failTail)
        {
            RegisterBank remote = Bank(0, 0, 126, 0), cache = Bank(0, 0, 126, 0);
            remote.Fill(2, 0x2222, false); cache.Fill(2, 0x1111, false);
            long revision = cache.Revision;
            FakeConnection channel = new FakeConnection(remote);
            ManualResetEvent tailEntered = new ManualResetEvent(false), tailRelease = new ManualResetEvent(false);
            channel.Handler = delegate(byte[] request)
            {
                if (request[0] == 3 && RegisterBank.Word(request, 1) == 135)
                {
                    tailEntered.Set(); tailRelease.WaitOne();
                    if (failTail) return new byte[] { 0x83, 2 };
                }
                return remote.Process(request);
            };
            MasterSession session = new MasterSession(cache, Options(), null, channel);
            try
            {
                Connected(session); session.ReadOnce();
                Wait(delegate { return tailEntered.WaitOne(0); }, "第二读取块等待");
                ushort[] pending = cache.ReadWords(2, 10, 126);
                foreach (ushort value in pending) Assert(value == 0x1111, "尾块等待时不能提交首块；跨125字边界值必须仍为完整旧值");
                Assert(cache.Revision == revision, "未完整读完前缓存不能发生修改");
                tailRelease.Set();
                if (failTail)
                {
                    Wait(delegate { return session.Errors == 1 && !session.IsBusy; }, "第二块失败");
                    foreach (ushort value in cache.ReadWords(2, 10, 126)) Assert(value == 0x1111, "尾块失败不能遗留前半区新值");
                    Assert(cache.Revision == revision && !session.AreaHasFreshData(2), "分块读取失败应完全保留原区与失败状态");
                }
                else
                {
                    Wait(delegate { return session.AreaHasFreshData(2) && !session.IsBusy; }, "整区一次提交");
                    foreach (ushort value in cache.ReadWords(2, 10, 126)) Assert(value == 0x2222, "所有块成功后整区应更新");
                    Assert(cache.Revision == revision + 1 && session.Requests == 2, "分块响应应通过一次缓存写操作提交");
                }
            }
            finally { tailRelease.Set(); session.Dispose(); tailEntered.Dispose(); tailRelease.Dispose(); }
        }

        private static void FailedReadback()
        {
            RegisterBank remote = Bank(0, 0, 1, 0), cache = Bank(0, 0, 1, 0);
            cache.Set(2, 10, 9);
            FakeConnection channel = new FakeConnection(remote);
            channel.Handler = delegate(byte[] request)
            { return request[0] == 3 ? new byte[] { 0x83, 2 } : remote.Process(request); };
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Connected(session); session.EnqueueWrite(2, 10, new ushort[] { 77 }, false);
                Wait(delegate { return session.Errors == 1 && !session.IsBusy; }, "异常回读处理完成");
                Assert(cache.Get(2, 10) == 9 && !session.AreaHasFreshData(2), "回读失败保留旧缓存");
                Assert(session.IsConnected && session.GetAreaStatus(2).Contains("读取失败") && session.GetAreaStatus(2).Contains("上次值"), "设备异常不得冒充物理断线");
                Assert(session.Requests == 2 && channel.Requests().Length == 2, "写失败不得自动重试");
            }
        }

        private static void PartialWritePreservesAreaState()
        {
            RegisterBank remote = Bank(0, 0, 2, 0), cache = Bank(0, 0, 2, 0);
            remote.Set(2, 10, 100); remote.Set(2, 11, 101); cache.Set(2, 11, 444);
            FakeConnection channel = new FakeConnection(remote);
            bool failWholeRead = false;
            channel.Handler = delegate(byte[] request)
            {
                if (request[0] == 3 && RegisterBank.Word(request, 3) == 2 && failWholeRead) return new byte[] { 0x83, 2 };
                // 让局部回读与整区读的时间戳可区分，验证局部写不刷新整区成功时间。
                if (request[0] == 3 && RegisterBank.Word(request, 3) == 1) Thread.Sleep(25);
                return remote.Process(request);
            };
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Connected(session); session.EnqueueWrite(2, 10, new ushort[] { 77 }, false);
                Wait(delegate { return session.Requests == 2 && !session.IsBusy; }, "初始局部写回读");
                Assert(!session.AreaHasFreshData(2), "未完整读取时，局部回读不得把整区快照标为新读值");
                Assert(cache.Get(2, 10) == 77 && cache.Get(2, 11) == 444, "局部回读只更新实际读取的地址");
                Assert(session.GetAreaStatus(2).Contains("局部") && session.GetAreaStatus(2).Contains("未读取"), "状态必须同时显示局部回读与整区未读取");

                session.ReadOnce(); Wait(delegate { return session.AreaHasFreshData(2) && !session.IsBusy; }, "完整读取");
                string wholeStatus = session.GetAreaStatus(2);
                session.EnqueueWrite(2, 10, new ushort[] { 78 }, false);
                Wait(delegate { return session.Requests == 5 && !session.IsBusy; }, "完整读取后的局部写");
                Assert(session.AreaHasFreshData(2) && session.GetAreaStatus(2).Contains(wholeStatus), "局部回读可保留整区fresh，但不得刷新完整读取时间");

                failWholeRead = true; session.ReadOnce();
                Wait(delegate { return session.Errors == 1 && !session.IsBusy; }, "整区读取失败");
                string failedStatus = session.GetAreaStatus(2);
                session.EnqueueWrite(2, 10, new ushort[] { 79 }, false);
                Wait(delegate { return session.Requests == 8 && !session.IsBusy; }, "失败后的局部写回读");
                Assert(!session.AreaHasFreshData(2), "整区失败后局部成功不得恢复整区fresh");
                Assert(session.GetAreaStatus(2).Contains(failedStatus) && session.GetAreaStatus(2).Contains("读取失败"), "局部成功不得清除其它地址的旧值/失败状态和完整读取时间");
                Assert(cache.Get(2, 10) == 79 && cache.Get(2, 11) == 101, "失败后局部回读仍可更新已确认地址");
            }
        }

        private static void WriteChunks()
        {
            RegisterBank remote = Bank(1969, 0, 124, 0), cache = Bank(1969, 0, 124, 0);
            FakeConnection channel = new FakeConnection(remote);
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Connected(session);
                ushort[] bits = new ushort[1969]; bits[1968] = 1;
                ushort[] words = new ushort[124]; words[123] = 7654;
                session.EnqueueWrite(0, 10, bits, true); session.EnqueueWrite(2, 10, words, true);
                Wait(delegate { return cache.Get(2, 133) == 7654 && !session.IsBusy; }, "批量写完成");
                byte[][] requests = channel.Requests();
                Assert(requests.Length == 8 && requests[0][0] == 15 && RegisterBank.Word(requests[0], 3) == 1968 && requests[2][0] == 15 && RegisterBank.Word(requests[2], 3) == 1, "位写1968分块，单元素尾块仍为0F");
                Assert(requests[4][0] == 16 && RegisterBank.Word(requests[4], 3) == 123 && requests[6][0] == 16 && RegisterBank.Word(requests[6], 3) == 1, "字写123分块，单元素尾块仍为10");
                Assert(requests[1][0] == 1 && requests[3][0] == 1 && requests[5][0] == 3 && requests[7][0] == 3, "每个写块必须立即回读");
                session.EnqueueWrite(0, 10, new ushort[] { 1 }, false);
                Wait(delegate { return session.Requests == 10 && !session.IsBusy; }, "单线圈写完成");
                Assert(channel.Requests()[8][0] == 5 && cache.Get(0, 1978) == 1, "单写使用05且尾值回读");
            }
        }

        private static void FailedAreaRead()
        {
            RegisterBank remote = Bank(0, 0, 1, 1), cache = Bank(0, 0, 1, 1);
            remote.Set(2, 10, 91); remote.Set(3, 10, 92); cache.Set(2, 10, 12);
            FakeConnection channel = new FakeConnection(remote);
            bool fail = false;
            channel.Handler = delegate(byte[] request)
            { return fail && request[0] == 3 ? new byte[] { 0x83, 2 } : remote.Process(request); };
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Connected(session); session.ReadOnce();
                Wait(delegate { return session.AreaHasFreshData(3) && !session.IsBusy; }, "首次读取");
                remote.Set(2, 10, 101); remote.Set(3, 10, 102); fail = true;
                session.ReadOnce();
                Wait(delegate { return session.Errors == 1 && !session.IsBusy; }, "区读取失败");
                Assert(cache.Get(2, 10) == 91 && cache.Get(3, 10) == 102, "失败区保留旧值，其他非空区继续读");
                Assert(!session.AreaHasFreshData(2) && session.AreaHasFreshData(3) && session.GetAreaStatus(2).Contains("上次成功"), "失败状态必须保留上次成功时间");
                Assert(session.IsConnected && session.Requests == 4, "设备异常不应关闭连接或额外重试");
            }
        }

        private static void QueueValidation()
        {
            RegisterBank remote = Bank(0, 0, 2, 0), cache = Bank(0, 0, 2, 0);
            FakeConnection channel = new FakeConnection(remote);
            ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false);
            int first = 0;
            channel.Handler = delegate(byte[] request)
            {
                if (Interlocked.Increment(ref first) == 1) { entered.Set(); release.WaitOne(); }
                return remote.Process(request);
            };
            using (MasterSession session = new MasterSession(cache, Options(), null, channel))
            {
                Reject(delegate { session.EnqueueWrite(2, 10, new ushort[] { 1 }, false); }, "未连接写");
                Connected(session); session.ReadOnce(); Wait(delegate { return entered.WaitOne(0); }, "占用事务");
                ushort[] value = { 22 }; session.EnqueueWrite(2, 10, value, false); value[0] = 33;
                Reject(delegate { session.EnqueueWrite(1, 10, new ushort[] { 1 }, false); }, "只读区写");
                Reject(delegate { session.EnqueueWrite(2, 9, new ushort[] { 1 }, false); }, "越界写");
                Reject(delegate { session.EnqueueWrite(2, 10, new ushort[] { 1, 2 }, false); }, "单写多个值");
                Reject(delegate { session.SetPolling(true, 49); }, "过快轮询");
                int queued = 1;
                try { for (; queued < 140; queued++) session.EnqueueWrite(2, 11, new ushort[] { 2 }, false); }
                catch (InvalidOperationException) { }
                Assert(queued <= 128 && queued > 1, "队列必须在128项内明确拒绝");
                Assert(channel.Requests().Length == 1, "已有事务未完成时不得并发发送队列写命令");
                release.Set(); Wait(delegate { return cache.Get(2, 10) == 22; }, "深拷贝数据执行");
                Assert(remote.Get(2, 10) == 22, "排队后调用方改数组不得改变线上值");
            }
            entered.Dispose(); release.Dispose();
        }

        private static void DisconnectAndCancel()
        {
            FakeConnection channel = new FakeConnection(Bank(0, 0, 1, 0));
            channel.Handler = delegate(byte[] request) { channel.Disconnect(); throw new IOException("测试传输断开"); };
            using (MasterSession session = new MasterSession(Bank(0, 0, 1, 0), Options(), null, channel))
            {
                Connected(session); session.SetPolling(true, 50);
                Wait(delegate { return !session.IsRunning; }, "断线退出");
                Assert(!session.IsPolling && !session.IsConnected && session.Errors == 1 && session.StateText.Contains("断"), "断线必须停止轮询并计错");
            }
            FakeConnection blocked = new FakeConnection(Bank(0, 0, 1, 0));
            blocked.BlockExecute = true;
            MasterSession cancel = new MasterSession(Bank(0, 0, 1, 0), Options(), null, blocked);
            Connected(cancel); cancel.ReadOnce();
            Wait(delegate { return blocked.ExecuteEntered.WaitOne(0); }, "阻塞IO");
            cancel.EnqueueWrite(2, 10, new ushort[] { 3 }, false);
            Stopwatch watch = Stopwatch.StartNew(); cancel.Dispose();
            Assert(watch.ElapsedMilliseconds < 2500 && !cancel.IsRunning && !cancel.IsPolling, "Dispose必须中断IO且有限等待");
            Assert(blocked.Requests().Length == 1, "取消后的写不得执行");
        }

        private static void ConfigurationRoundtrip()
        {
            string path = Path.Combine(Path.GetTempPath(), "modbus-master-config-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                RegisterBank bank = Bank(0, 0, 2, 0); bank.Set(2, 10, 42);
                MasterConfiguration config = new MasterConfiguration { Display = SimulatorConfiguration.Capture(bank), PollIntervalMs = 550 };
                config.Display.Areas[2].ItemLabels = new AddressLabel[] { new AddressLabel { Address = 10, Name = "温度", Unit = "℃" } };
                config.Display.CardOrder = new CardKey[] { new CardKey { Area = 2, Address = 10 } };
                config.Save(path); string saved = File.ReadAllText(path);
                MasterConfiguration loaded = MasterConfiguration.Load(path);
                Assert(loaded.Version == 1 && loaded.PollIntervalMs == 550 && loaded.Display.CreateBank().Get(2, 10) == 42, "配置值应往返");
                Assert(loaded.Display.Areas[2].ItemLabels[0].Name == "温度" && loaded.Display.CardOrder[0].Address == 10, "名称单位及卡片顺序应往返");
                config.PollIntervalMs = 1; Reject(delegate { config.Save(path); }, "非法配置保存");
                Assert(File.ReadAllText(path) == saved, "保存验证失败不得破坏已存在配置");
                File.WriteAllText(path, "{\"Version\":9}"); Reject(delegate { MasterConfiguration.Load(path); }, "配置版本错误");
                using (FileStream oversized = new FileStream(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(32L * 1024 * 1024 + 1);
                Reject(delegate { MasterConfiguration.Load(path); }, "超过32MB配置");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private sealed class FakeConnection : IMasterConnection
        {
            private readonly RegisterBank remote;
            private readonly object gate = new object();
            private readonly List<byte[]> requests = new List<byte[]>();
            private volatile bool connected;
            private volatile bool disposed;
            public readonly ManualResetEvent ConnectEntered = new ManualResetEvent(false);
            public readonly ManualResetEvent ConnectRelease = new ManualResetEvent(true);
            public readonly ManualResetEvent ExecuteEntered = new ManualResetEvent(false);
            private readonly ManualResetEvent executeRelease = new ManualResetEvent(false);
            public Exception ConnectFailure;
            public bool BlockExecute;
            public Func<byte[], byte[]> Handler;
            public FakeConnection(RegisterBank remote) { this.remote = remote; }
            public bool IsConnected { get { return connected; } }
            public void Connect()
            {
                ConnectEntered.Set(); ConnectRelease.WaitOne();
                if (disposed) throw new ObjectDisposedException("FakeConnection");
                if (ConnectFailure != null) throw ConnectFailure;
                connected = true;
            }
            public byte[] Execute(byte unit, byte[] pdu)
            {
                lock (gate) requests.Add((byte[])pdu.Clone());
                ExecuteEntered.Set();
                if (BlockExecute) executeRelease.WaitOne();
                if (disposed || !connected) throw new IOException("假通道已关闭");
                return Handler == null ? remote.Process(pdu) : Handler(pdu);
            }
            public byte[][] Requests() { lock (gate) return requests.ToArray(); }
            public void Disconnect() { connected = false; }
            public void Dispose()
            { disposed = true; connected = false; ConnectRelease.Set(); executeRelease.Set(); }
        }
    }
}
