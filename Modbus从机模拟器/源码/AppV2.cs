using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("Modbus从机模拟器 2.9")]
[assembly: AssemblyVersion("2.9.0.0")]
namespace ModbusSimulator
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [STAThread] private static void Main()
        {
            SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
    public sealed partial class MainForm : Form
    {
        private RegisterBank bank = CreateInitialBank();
        private SimulatorServer server;
        private readonly Queue<string> pendingLogs = new Queue<string>();
        private readonly ComboBox mode = new ComboBox(), com = new ComboBox(), baud = new ComboBox(), parity = new ComboBox(), stopBits = new ComboBox(), dataBits = new ComboBox();
        private readonly TextBox ip = new TextBox();
        private readonly NumericUpDown port = new NumericUpDown(), unit = new NumericUpDown();
        private readonly Button toggle = Ui.Button("启动从机", 116, true), load = Ui.Button("加载配置", 100, false);
        private readonly Label state = new Label(), tip = new Label(), counts = new Label();
        private readonly CompactStatusLabel notice = new CompactStatusLabel();
        private readonly TrafficLog logs = new TrafficLog();
        private Icon softwareIcon;
        public readonly RawSendPanel rawSender = new RawSendPanel();
        private DateTime rawTargetsRefreshed = DateTime.MinValue;
        private readonly Panel settings = new Panel();
        private readonly CheckBox pause = new CheckBox { Text = "暂停显示", AutoSize = true };
        private readonly Timer timer = new Timer();
        private long dropped;
        private volatile int pendingRawSends;
        private readonly Queue<RawSendWork> rawSendQueue = new Queue<RawSendWork>();
        private bool rawSendWorkerActive;
        private sealed class RawSendWork { public SimulatorServer Server; public string Target; public byte[] Data; }
        private bool interactiveResize;
        public readonly AreaPanel[] Areas = new AreaPanel[4];
        public RegisterBank Bank { get { return bank; } }
        public MainForm() : this(UiLayoutState.DefaultPath("slave")) { }
        public MainForm(string layoutStatePath)
        {
            uiLayoutPath = layoutStatePath;
            Button save, refreshCom, clear, export;
            BuildMainUi(out save, out refreshCom, out clear, out export);
            DoubleBuffered = true;
            softwareIcon = AppAssets.LoadIcon(); Icon = softwareIcon;
            rawSender.IsSendBusy = delegate { return pendingRawSends != 0; };
            rawSender.GetTargets = delegate { return server == null ? new string[0] : server.GetRawTargets(); };
            rawSender.SendRequested = delegate(string target, byte[] bytes)
            {
                SimulatorServer current = server; if (current == null || !current.IsRunning) { AddLog("自定义发送失败：请先启动从机并建立连接。"); return false; }
                byte[] owned = (byte[])bytes.Clone();
                lock (rawSendQueue)
                {
                    if (pendingRawSends >= 16) { AddLog("自定义发送队列已满，请稍后重试。"); return false; }
                    rawSendQueue.Enqueue(new RawSendWork { Server = current, Target = target, Data = owned }); pendingRawSends++;
                    if (!rawSendWorkerActive) { rawSendWorkerActive = true; System.Threading.ThreadPool.QueueUserWorkItem(delegate { RunRawSendQueue(); }); }
                }
                return true;
            }; rawSender.RefreshTargets();
            ResizeBegin += delegate { interactiveResize = true; Views.BeginInteractiveResize(); };
            ResizeEnd += delegate { interactiveResize = false; Views.EndInteractiveResize(); TickUi(); };
            mode.SelectedIndexChanged += delegate { UpdateMode(); }; com.DropDown += delegate { RefreshPorts(); }; refreshCom.Click += delegate { RefreshPorts(); };
            toggle.Click += Toggle; save.Click += SaveConfiguration; load.Click += LoadConfiguration;
            clear.Click += delegate { logs.Clear(); lock (pendingLogs) { pendingLogs.Clear(); dropped = 0; } }; export.Click += ExportLogs;
            timer.Interval = 200; timer.Tick += delegate { TickUi(); }; timer.Start();
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (server != null && LocalizedMessageBox.Show(this, "从机正在运行，退出会断开连接。是否退出？", "退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { e.Cancel = true; return; }
                timer.Stop(); CancelQueuedRawSends(); if (server != null) { server.Dispose(); server = null; }
            };
            UpdateMode(); AddLog("就绪：支持ASCII、卡片解析及五种视图；每区默认10项。日志可Ctrl+C复制。");
        }
        // 单worker按用户提交顺序发送；同目标锁只保证不交错，本队列进一步保证FIFO。
        private void RunRawSendQueue()
        {
            while (true)
            {
                RawSendWork item;
                lock (rawSendQueue)
                {
                    if (rawSendQueue.Count == 0) { rawSendWorkerActive = false; return; }
                    item = rawSendQueue.Dequeue();
                }
                try { item.Server.SendRaw(item.Target, item.Data); }
                catch (Exception ex)
                {
                    string message = "自定义发送失败：" + ex.Message; AddLog(message);
                    try { if (!IsDisposed && !Disposing && IsHandleCreated) BeginInvoke(new Action(delegate { if (!IsDisposed) rawSender.StopCycle(message); })); }
                    catch (InvalidOperationException) { }
                }
                finally { lock (rawSendQueue) pendingRawSends--; }
            }
        }
        private void CancelQueuedRawSends()
        { lock (rawSendQueue) { pendingRawSends -= rawSendQueue.Count; rawSendQueue.Clear(); } }
        private static void ConfigureCombo(ComboBox box, string[] values)
        { box.Dock = DockStyle.Fill; box.DropDownStyle = ComboBoxStyle.DropDownList; box.Items.AddRange(values); if (values.Length > 0) box.SelectedIndex = 0; }
        private static void Field(TableLayoutPanel parent, int column, string title, Control input)
        {
            TableLayoutPanel field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 10, 0) };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            field.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            field.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(100, 116, 139) }, 0, 0);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(0); field.Controls.Add(input, 0, 1); parent.Controls.Add(field, column, 0);
        }
        private void RefreshPorts()
        {
            if (server != null) { AddLog("运行中请先停止，再刷新串口。"); return; }
            string selected = com.Text;
            try { selected = SerialDevices.RawPort(selected); } catch (ArgumentException) { }
            com.BeginUpdate();
            try { com.Items.Clear(); com.Items.AddRange(SerialDevices.Enumerate()); com.DropDownWidth = 400; SelectPort(selected); }
            finally { com.EndUpdate(); }
        }
        private void SelectPort(string portName)
        {
            foreach (object item in com.Items)
            {
                SerialDevice device = item as SerialDevice;
                if (device != null && String.Equals(device.PortName, portName, StringComparison.OrdinalIgnoreCase)) { com.SelectedItem = item; return; }
            }
            if (!String.IsNullOrEmpty(portName)) com.Text = portName;
            else if (com.Items.Count > 0) com.SelectedIndex = 0;
            else com.Text = "";
        }
        private static RegisterBank CreateInitialBank()
        {
            RegisterBank result = new RegisterBank();
            for (int area = 0; area < 4; area++) result.Configure(area, 0, 10);
            return result;
        }
        private int TransportMode { get { return TransportCatalog.IdAt(mode.SelectedIndex); } }
        public void SelectTransport(int id) { mode.SelectedIndex = TransportCatalog.IndexOf(id); }
        private void UpdateMode()
        {
            bool tcp = IsNetworkMode;
            ip.Visible = tcp; com.Visible = !tcp; if (!tcp) com.BringToFront(); else ip.BringToFront();
            port.Enabled = tcp; baud.Enabled = parity.Enabled = stopBits.Enabled = !tcp; dataBits.Enabled = TransportMode == 3;
            if (TransportMode != 3) dataBits.SelectedItem = "8";
            if (!tcp && com.Items.Count == 0) RefreshPorts();
            tip.Text = tcp ? (TransportMode == 0 ? "TCP/IP：MBAP + PDU，无CRC。" : TransportMode == 4 ? "UDP/IP：每数据报一个MBAP帧，无CRC。" :
                TransportMode == 5 ? "RTU Over TCP/IP：带CRC的RTU字节流，支持拆包/粘包。" : "RTU Over UDP/IP：每数据报一个RTU帧，包含CRC。") + " 本机主站可连接127.0.0.1。" :
                TransportMode == 1 ? "RTU：8 数据位，自动校验 CRC；RS-485 使用自动收发转换器。四区同时有效。" :
                TransportMode == 2 ? "串口 MBAP 扩展：MBAP + PDU，不加 RTU CRC。按 Length 收帧，按 TID 对应响应。" :
                "ASCII：冒号 + HEX + LRC + CRLF，可选7/8数据位。请与对端的校验和停止位保持一致。";
        }
        private bool IsNetworkMode { get { return TransportMode == 0 || TransportMode >= 4; } }
        private bool CommitAll()
        {
            if (!Views.Commit()) { notice.Text = "请先完成列表中的编辑。"; return false; }
            foreach (AreaPanel panel in Areas)
            {
                if (!panel.Commit()) { notice.Text = "请完成或按 Esc 取消无效的单元格编辑。"; panel.Grid.Focus(); return false; }
                if (panel.HasPendingDefinition)
                {
                    selectedArea.SelectedIndex = panel.Area == 2 ? 0 : panel.Area == 3 ? 1 : panel.Area == 0 ? 2 : 3;
                    notice.Text = "有尚未应用的数据区定义，请点击该区“应用定义”后再启动或保存。"; panel.CountInput.Focus(); return false;
                }
            }
            return true;
        }
        private void SetRunning(bool running)
        {
            settings.Enabled = !running; load.Enabled = !running; foreach (AreaPanel panel in Areas) panel.SetRunning(running);
            toggle.Text = running ? "停止从机" : "启动从机";
            state.Text = running ? "● 运行中 · " + (IsNetworkMode ? ip.Text + ":" + port.Value : com.Text) + " · 站号 " + unit.Value : "● 已停止 · 数据保留";
            state.ForeColor = running ? UiTheme.SuccessText : UiTheme.MutedText;
        }
        private void Toggle(object sender, EventArgs e)
        {
            rawSender.StopCycle(null);
            if (server != null) { CancelQueuedRawSends(); server.Dispose(); server = null; SetRunning(false); AddLog("服务已停止，数据保留。"); return; }
            if (!CommitAll()) return;
            try
            {
                SimulatorServer candidate = new SimulatorServer(bank, (byte)unit.Value, AddLog);
                try
                {
                    if (IsNetworkMode)
                    {
                        IPAddress address;
                        if (!IPAddress.TryParse(ip.Text.Trim(), out address)) throw new ArgumentException("监听 IP 无效。");
                        if (TransportMode == 0) candidate.StartTcp(address, (int)port.Value);
                        else if (TransportMode == 5) candidate.StartRtuTcp(address, (int)port.Value);
                        else candidate.StartUdp(address, (int)port.Value, TransportMode == 6);
                    }
                    else
                    {
                        if (String.IsNullOrWhiteSpace(com.Text)) throw new ArgumentException("请选择 COM 口。");
                        if (TransportMode == 3) candidate.StartAscii(SerialDevices.RawPort(com.Text), int.Parse(baud.Text), (Parity)Enum.Parse(typeof(Parity), parity.Text), stopBits.Text == "2" ? StopBits.Two : StopBits.One, int.Parse(dataBits.Text));
                        else candidate.StartSerial(SerialDevices.RawPort(com.Text), int.Parse(baud.Text), (Parity)Enum.Parse(typeof(Parity), parity.Text), stopBits.Text == "2" ? StopBits.Two : StopBits.One, TransportMode == 2);
                    }
                }
                catch { candidate.Dispose(); throw; }
                server = candidate; SetRunning(true);
            }
            catch (Exception ex) { AddLog("启动失败：" + ex.Message); LocalizedMessageBox.Show(this, ex.Message, "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        public SimulatorConfiguration CaptureConfiguration()
        {
            if (!CommitAll()) throw new ArgumentException("请先完成单元格编辑，并应用尚未生效的数据区定义。");
            SimulatorConfiguration config = SimulatorConfiguration.Capture(bank);
            config.Mode = TransportMode; config.Ip = ip.Text.Trim(); config.Port = (int)port.Value; config.Unit = (int)unit.Value;
            config.Com = String.IsNullOrWhiteSpace(com.Text) ? "" : SerialDevices.RawPort(com.Text); config.Baud = int.Parse(baud.Text); config.Parity = parity.Text; config.StopBits = stopBits.Text;
            config.DataBits = int.Parse(dataBits.Text); config.CardOrder = Views.Cards.CustomOrder;
            for (int area = 0; area < 4; area++) { config.Areas[area].DefaultRule = AreaPresentation.Copy(Areas[area].Presentation.DefaultRule); config.Areas[area].ItemRules = Areas[area].Presentation.Export(); config.Areas[area].ItemLabels = Areas[area].Presentation.ExportLabels(); }
            config.Validate(); return config;
        }
        public void ApplyConfiguration(SimulatorConfiguration config)
        {
            if (server != null) throw new InvalidOperationException("请先停止从机。");
            // 先完整构建再替换，损坏配置不改变现有数据。
            RegisterBank replacement = config.CreateBank();
            Views.ListGrid.CancelEdit(); foreach (AreaPanel panel in Areas) panel.Grid.CancelEdit(); bank = replacement;
            ip.Text = config.Ip; port.Value = config.Port; unit.Value = config.Unit; baud.Text = config.Baud.ToString(); parity.Text = config.Parity; stopBits.Text = config.StopBits;
            SelectTransport(config.Mode); dataBits.SelectedItem = config.DataBits.ToString(); SelectPort(config.Com);
            foreach (AreaPanel panel in Areas) { panel.Presentation.Import(config.Areas[panel.Area]); panel.Reload(bank); } Views.Cards.CustomOrder = config.CardOrder; Views.Reload();
            notice.Text = "配置与数据已加载；选择显示方式切换视图，启动前确认连接参数。";
            AddLog("配置与四区数据已加载；服务保持停止。");
        }
        private void SaveConfiguration(object sender, EventArgs e)
        {
            try
            {
                SimulatorConfiguration config = CaptureConfiguration();
                using (SaveFileDialog dialog = new SaveFileDialog { Filter = "模拟器配置 (*.json)|*.json", FileName = "从机配置.json" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { config.Save(dialog.FileName); notice.Text = "配置已保存：" + dialog.FileName; AddLog(notice.Text); }
            }
            catch (Exception ex) { LocalizedMessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void LoadConfiguration(object sender, EventArgs e)
        {
            if (server != null) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "模拟器配置 (*.json)|*.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SimulatorConfiguration config = SimulatorConfiguration.Load(dialog.FileName);
                    if (LocalizedMessageBox.Show(this, "将替换连接参数、四区定义和全部数据。请先保存需要保留的配置。", "加载配置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    { ApplyConfiguration(config); notice.Text = "已加载：" + dialog.FileName; }
                }
                catch (Exception ex) { LocalizedMessageBox.Show(this, ex.Message, "配置无效，原数据保留", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
        private void ExportLogs(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "文本日志 (*.txt)|*.txt", FileName = "Modbus日志.txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, logs.CopyText(true), Encoding.UTF8); notice.Text = "已导出当前显示的 " + logs.RecordCount + " 条日志。";
                }
                catch (Exception ex) { LocalizedMessageBox.Show(this, ex.Message, "导出失败"); }
            }
        }
        private void AddLog(string text)
        {
            lock (pendingLogs)
            {
                while (pendingLogs.Count >= 2000) { pendingLogs.Dequeue(); dropped++; }
                pendingLogs.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text);
            }
        }
        private void TickUi()
        {
            if (interactiveResize) return;
            if (server != null && !server.IsRunning) { server.Dispose(); server = null; SetRunning(false); AddLog("通信服务已停止，请检查日志后重新启动。"); }
            int enabledAreas = 0; for (int area = 0; area < 4; area++) if (bank.Count(area) > 0) enabledAreas++;
            counts.Text = "处理 " + (server == null ? 0 : server.Requests) + " 条 · 启用 " + enabledAreas + " 区";
            if (!pause.Checked)
            {
                List<string> batch = new List<string>();
                lock (pendingLogs)
                {
                    if (dropped != 0) { batch.Add("日志显示队列已丢弃 " + dropped + " 条早期记录；不影响通信"); dropped = 0; }
                    for (int i = 0; i < 250 && pendingLogs.Count > 0; i++) batch.Add(pendingLogs.Dequeue());
                }
                if (batch.Count > 0) logs.AppendLines(batch);
            }
            rawSender.Enabled = server != null && server.IsRunning;
            if ((DateTime.UtcNow - rawTargetsRefreshed).TotalSeconds >= 1) { rawSender.RefreshTargets(); rawTargetsRefreshed = DateTime.UtcNow; }
            Views.RefreshValues();
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { timer.Dispose(); CancelQueuedRawSends(); if (server != null) { server.Dispose(); server = null; } } base.Dispose(disposing); if (disposing && softwareIcon != null) { softwareIcon.Dispose(); softwareIcon = null; } }
    }
}
