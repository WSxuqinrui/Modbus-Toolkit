using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
[assembly: AssemblyTitle("Modbus主机调试工具 2.9")]
[assembly: AssemblyVersion("2.9.0.0")]
namespace ModbusMaster
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [STAThread] private static void Main()
        { SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm()); }
    }
    public sealed partial class MainForm : Form
    {
        private RegisterBank bank = InitialBank();
        private MasterSession session;
        private readonly Queue<string> pendingLogs = new Queue<string>();
        private readonly ComboBox mode = new ComboBox(), com = new ComboBox(), baud = new ComboBox(), parity = new ComboBox(), stopBits = new ComboBox(), dataBits = new ComboBox();
        private readonly TextBox ip = new TextBox();
        private readonly NumericUpDown port = new NumericUpDown(), unit = new NumericUpDown(), timeoutMs = new NumericUpDown(), pollInterval = new NumericUpDown();
        private readonly Button toggle = Ui.Button("连接从机", 116, true), load = Ui.Button("加载配置", 100, false);
        private readonly Button readOnce = Ui.Button("读取一次", 85, false), poll = Ui.Button("自动轮询", 85, true), write = Ui.Button("写入…", 75, false);
        private readonly Label state = new Label(), tip = new Label(), counts = new Label();
        private readonly CompactStatusLabel notice = new CompactStatusLabel();
        private readonly TrafficLog logs = new TrafficLog();
        private Icon softwareIcon;
        public readonly RawSendPanel rawSender = new RawSendPanel();
        private DateTime rawTargetsRefreshed = DateTime.MinValue;
        private readonly Panel settings = new Panel();
        private readonly CheckBox pause = new CheckBox { Text = "暂停显示", AutoSize = true };
        private readonly Timer timer = new Timer();
        private bool resizing, lastConnected;
        private long dropped;
        public readonly AreaPanel[] Areas = new AreaPanel[4];
        public RegisterBank Bank { get { return bank; } }
        public MasterSession Session { get { return session; } }
        public MainForm() : this(UiLayoutState.DefaultPath("master")) { }
        public MainForm(string layoutStatePath)
        {
            uiLayoutPath = layoutStatePath;
            Button save, refresh, clear, export; BuildMainUi(out save, out refresh, out clear, out export); DoubleBuffered = true;
            softwareIcon = AppAssets.LoadIcon(); Icon = softwareIcon;
            rawSender.IsSendBusy = delegate { return session == null || session.HasPendingWork; };
            rawSender.GetTargets = delegate { return session != null && session.IsConnected ? new string[] { NetworkMode ? ip.Text.Trim() + ":" + port.Value : com.Text } : new string[0]; };
            rawSender.SendRequested = delegate(string target, byte[] bytes)
            {
                try { if (session == null) throw new InvalidOperationException("请先连接从机。"); session.EnqueueRaw(bytes, 500); UpdateControls(); return true; }
                catch (Exception ex) { AddLog("自定义发送失败：" + ex.Message); return false; }
            }; rawSender.RefreshTargets();
            for (int i = 0; i < Areas.Length; i++)
            {
                int area = i; Areas[i].RequestWrite = delegate(int address, ushort[] words, bool multiple) { return QueueWrite(area, address, words, multiple); };
                Areas[i].DefinitionChanged += delegate { MarkCached("未读取（定义已改变）"); };
            }
            mode.SelectedIndexChanged += delegate { UpdateMode(); }; refresh.Click += delegate { RefreshPorts(); }; com.DropDown += delegate { RefreshPorts(); };
            toggle.Click += delegate
            {
                try { if (session != null && session.IsRunning) Disconnect(); else Connect(); }
                catch (Exception ex) { AddLog("连接失败：" + ex.Message); notice.Text = ex.Message; LocalizedMessageBox.Show(this, ex.Message, "连接参数"); }
            };
            readOnce.Click += delegate { try { ReadOnce(); } catch (Exception ex) { AddLog("读取失败：" + ex.Message); notice.Text = ex.Message; } };
            poll.Click += delegate { try { SetPolling(session != null && !session.IsPolling); } catch (Exception ex) { AddLog("轮询失败：" + ex.Message); } };
            pollInterval.ValueChanged += delegate { if (session != null && session.IsPolling) session.SetPolling(true, (int)pollInterval.Value); };
            write.Click += delegate { using (WriteDialog dialog = new WriteDialog()) if (dialog.ShowDialog(this) == DialogResult.OK) QueueWrite(dialog.Area, dialog.Address, dialog.Values, dialog.Multiple); };
            save.Click += SaveConfiguration; load.Click += LoadConfiguration; export.Click += ExportLogs;
            clear.Click += delegate { logs.Clear(); lock (pendingLogs) { pendingLogs.Clear(); dropped = 0; } };
            ResizeBegin += delegate { resizing = true; Views.BeginInteractiveResize(); };
            ResizeEnd += delegate { resizing = false; Views.EndInteractiveResize(); TickUi(); };
            FormClosing += delegate { timer.Stop(); Disconnect(); };
            timer.Interval = 150; timer.Tick += delegate { TickUi(); }; timer.Start();
            UpdateMode(); MarkCached("未读取（本地缓存）"); UpdateControls();
            AddLog("就绪：配置数据为缓存；连接后读取。支持七种通信模式、自动轮询及05/06/0F/10写入；写入确认后读回刷新。");
        }
        private static RegisterBank InitialBank()
        { RegisterBank result = new RegisterBank(); for (int area = 0; area < 4; area++) result.Configure(area, 0, 10); return result; }
        private static void ConfigureCombo(ComboBox box, string[] values)
        { box.Dock = DockStyle.Fill; box.DropDownStyle = ComboBoxStyle.DropDownList; box.Items.AddRange(values); if (values.Length > 0) box.SelectedIndex = 0; }
        private int TransportMode { get { return TransportCatalog.IdAt(mode.SelectedIndex); } }
        public void SelectTransport(int id) { mode.SelectedIndex = TransportCatalog.IndexOf(id); }
        private bool NetworkMode { get { return TransportMode == 0 || TransportMode >= 4; } }
        private void RefreshPorts()
        {
            if (session != null && session.IsRunning) return;
            string selected = com.Text; try { selected = SerialDevices.RawPort(selected); } catch (ArgumentException) { }
            com.BeginUpdate(); try { com.Items.Clear(); com.Items.AddRange(SerialDevices.Enumerate()); com.DropDownWidth = 400; SelectPort(selected); } finally { com.EndUpdate(); }
        }
        private void SelectPort(string portName)
        {
            foreach (object item in com.Items)
            { SerialDevice device = item as SerialDevice; if (device != null && String.Equals(device.PortName, portName, StringComparison.OrdinalIgnoreCase)) { com.SelectedItem = item; return; } }
            if (!String.IsNullOrEmpty(portName)) com.Text = portName; else if (com.Items.Count > 0) com.SelectedIndex = 0; else com.Text = "";
        }
        private void UpdateMode()
        {
            bool network = NetworkMode; ip.Visible = network; com.Visible = !network; if (network) ip.BringToFront(); else com.BringToFront();
            port.Enabled = network; baud.Enabled = parity.Enabled = stopBits.Enabled = !network; dataBits.Enabled = TransportMode == 3;
            if (TransportMode != 3) dataBits.SelectedItem = "8";
            if (!network && com.Items.Count == 0) RefreshPorts();
            tip.Text = network ? "远端IP/端口须与从机一致；本机联调使用127.0.0.1。TCP/UDP与RTU封装不可混用。" :
                TransportMode == 3 ? "ASCII为冒号+HEX+LRC+CRLF；可选7/8数据位，校验和停止位与设备一致。" :
                TransportMode == 2 ? "串口MBAP扩展无CRC，严格按Length收帧并核对Transaction ID。" : "RTU使用8数据位与CRC16；波特率、校验、停止位与设备一致。";
        }
        private MasterConnectionOptions CaptureOptions()
        {
            string rawCom = "";
            if (!String.IsNullOrWhiteSpace(com.Text))
            {
                try { rawCom = SerialDevices.RawPort(com.Text); }
                catch (ArgumentException) { if (!NetworkMode) throw; }
            }
            return new MasterConnectionOptions { Mode = TransportMode, Host = ip.Text.Trim(), Port = (int)port.Value, Unit = (int)unit.Value,
                Com = rawCom, Baud = int.Parse(baud.Text), Parity = parity.Text,
                StopBits = stopBits.Text, DataBits = int.Parse(dataBits.Text), TimeoutMs = (int)timeoutMs.Value };
        }
        private bool CommitAll()
        {
            if (!Views.Commit()) return false;
            foreach (AreaPanel panel in Areas)
            {
                if (!panel.Commit()) return false;
                if (panel.HasPendingDefinition) { notice.Text = "请先应用数据区定义。"; return false; }
            }
            return true;
        }
        public void Connect()
        {
            if (session != null && session.IsRunning) throw new InvalidOperationException("请先断开当前连接。");
            if (!CommitAll()) throw new ArgumentException("请完成编辑并应用数据区定义。");
            if (session != null) session.Dispose(); MasterConnectionOptions options = CaptureOptions(); options.Validate();
            session = new MasterSession(bank, options, AddLog); lastConnected = false; MarkCached("连接中，显示上次值"); session.Start(); UpdateControls();
        }
        public void Disconnect()
        {
            rawSender.StopCycle(null);
            if (session != null) { session.Dispose(); session = null; AddLog("已断开，显示值为上次缓存。"); }
            lastConnected = false; if (!IsDisposed && Views != null) { MarkCached("已断开 · 上次值"); UpdateControls(); }
        }
        public void ReadOnce()
        { if (session == null || !session.IsConnected) throw new InvalidOperationException("请先连接从机。"); session.ReadOnce(); }
        public void SetPolling(bool enabled)
        { if (session == null || !session.IsConnected) throw new InvalidOperationException("请先连接从机。"); session.SetPolling(enabled, (int)pollInterval.Value); UpdateControls(); }
        public bool QueueWrite(int area, int address, ushort[] values, bool multiple)
        {
            try
            {
                if (session == null || !session.IsConnected) throw new ArgumentException("请先连接从机。");
                session.EnqueueWrite(area, address, values, multiple); notice.Text = "写入已排队；确认后读回刷新。"; return true;
            }
            catch (Exception ex) { AddLog("写入失败：" + ex.Message); notice.Text = ex.Message; return false; }
        }
        private void MarkCached(string reason) { foreach (AreaPanel panel in Areas) panel.SetQuality(reason, false); if (Views != null) Views.Cards.RefreshValues(); }
        private void UpdateControls()
        {
            bool connected = session != null && session.IsConnected, active = session != null && session.IsRunning;
            settings.Enabled = !active; load.Enabled = !active; toggle.Text = active ? "断开连接" : "连接从机";
            bool typed = connected && !session.IsRawMode;
            readOnce.Enabled = poll.Enabled = write.Enabled = typed; rawSender.Enabled = connected; poll.Text = session != null && session.IsPolling ? "停止轮询" : "自动轮询";
            foreach (AreaPanel panel in Areas) { panel.SetRunning(active); panel.SetWriteEnabled(typed); }
            state.Text = "● " + (session == null ? "未连接 · 显示缓存" : session.StateText); state.AutoEllipsis = true;
            state.ForeColor = connected ? UiTheme.SuccessText : UiTheme.MutedText;
            counts.Text = session == null ? "未连接" : "请求 " + session.Requests + " · 错误 " + session.Errors;
        }
        public MasterConfiguration CaptureConfiguration()
        {
            if (!CommitAll()) throw new ArgumentException("请先完成编辑并应用数据区定义。");
            MasterConnectionOptions options = CaptureOptions(); SimulatorConfiguration display = SimulatorConfiguration.Capture(bank);
            display.Mode = options.Mode; display.Ip = options.Host; display.Port = options.Port; display.Unit = options.Unit; display.Com = options.Com;
            display.Baud = options.Baud; display.Parity = options.Parity; display.StopBits = options.StopBits; display.DataBits = options.DataBits; display.CardOrder = Views.Cards.CustomOrder;
            foreach (AreaPanel panel in Areas) { display.Areas[panel.Area].DefaultRule = AreaPresentation.Copy(panel.Presentation.DefaultRule); display.Areas[panel.Area].ItemRules = panel.Presentation.Export(); display.Areas[panel.Area].ItemLabels = panel.Presentation.ExportLabels(); }
            MasterConfiguration config = new MasterConfiguration { Connection = options, Display = display, PollIntervalMs = (int)pollInterval.Value }; config.Validate(); return config;
        }
        public void ApplyConfiguration(MasterConfiguration config)
        {
            if (session != null && session.IsRunning) throw new InvalidOperationException("请先断开连接。");
            config.Validate(); RegisterBank replacement = config.Display.CreateBank(); Disconnect();
            Views.ListGrid.CancelEdit(); foreach (AreaPanel panel in Areas) panel.Grid.CancelEdit(); bank = replacement;
            MasterConnectionOptions options = config.Connection; ip.Text = options.Host; port.Value = options.Port; unit.Value = options.Unit;
            baud.Text = options.Baud.ToString(); parity.Text = options.Parity; stopBits.Text = options.StopBits; SelectTransport(options.Mode);
            dataBits.SelectedItem = options.DataBits.ToString(); SelectPort(options.Com); timeoutMs.Value = options.TimeoutMs; pollInterval.Value = config.PollIntervalMs;
            foreach (AreaPanel panel in Areas) { panel.Presentation.Import(config.Display.Areas[panel.Area]); panel.Reload(bank); }
            Views.Cards.CustomOrder = config.Display.CardOrder; Views.Reload(); MarkCached("配置快照，尚未读取"); UpdateControls();
            AddLog("配置已加载；保持断开，未自动读取或写入设备。");
        }
        private void SaveConfiguration(object sender, EventArgs e)
        {
            try { MasterConfiguration config = CaptureConfiguration(); using (SaveFileDialog dialog = new SaveFileDialog { Filter = "主机配置 (*.json)|*.json", FileName = "主机配置.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) { config.Save(dialog.FileName); AddLog("配置已保存：" + dialog.FileName); } }
            catch (Exception ex) { LocalizedMessageBox.Show(this, ex.Message, "保存失败"); }
        }
        private void LoadConfiguration(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "主机配置 (*.json)|*.json" })
                if (dialog.ShowDialog(this) == DialogResult.OK) try { ApplyConfiguration(MasterConfiguration.Load(dialog.FileName)); } catch (Exception ex) { LocalizedMessageBox.Show(this, ex.Message, "加载失败，旧配置保留"); }
        }
        private void ExportLogs(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "文本日志 (*.txt)|*.txt", FileName = "Modbus主机日志.txt" })
                if (dialog.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(dialog.FileName, logs.CopyText(true), new UTF8Encoding(false)); } catch (Exception ex) { LocalizedMessageBox.Show(this, ex.Message); }
        }
        private void AddLog(string text)
        {
            lock (pendingLogs) { while (pendingLogs.Count >= 2000) { pendingLogs.Dequeue(); dropped++; } pendingLogs.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text); }
        }
        private void TickUi()
        {
            if (resizing) return; bool connected = session != null && session.IsConnected;
            if (connected && !lastConnected) { try { session.ReadOnce(); } catch (Exception ex) { AddLog("读取失败：" + ex.Message); } }
            if (!connected && lastConnected) { Views.ListGrid.CancelEdit(); foreach (AreaPanel panel in Areas) panel.Grid.CancelEdit(); }
            lastConnected = connected; UpdateControls();
            if (session != null) foreach (AreaPanel panel in Areas) panel.SetQuality((!connected ? "未连接 · " : session.IsRawMode ? "原始模式，上次值 · " : "") + session.GetAreaStatus(panel.Area), connected && !session.IsRawMode && session.AreaHasFreshData(panel.Area));
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
            if ((DateTime.UtcNow - rawTargetsRefreshed).TotalSeconds >= 1) { rawSender.RefreshTargets(); rawTargetsRefreshed = DateTime.UtcNow; }
            Views.RefreshValues(); if (Views.CurrentMode == 4) Views.Cards.RefreshValues();
            List<string> quality = new List<string>(), summary = new List<string>(); string[] shortNames = { "线圈", "离散", "保持", "输入" };
            foreach (int area in new int[] { 2, 3, 0, 1 }) if (bank.Count(area) > 0)
            {
                quality.Add(AreaPanel.Names[area] + "：" + Areas[area].QualityText);
                summary.Add(shortNames[area] + "：" + (!connected ? "缓存" : session.IsRawMode ? "上次值" : Areas[area].HasFreshData ? "已更新" : "未更新"));
            }
            notice.SetStatus((!connected ? "未连接" : session.IsRawMode ? "原始发送" : "已连接") + " · " + (summary.Count == 0 ? "未启用数据区" : String.Join("  |  ", summary.ToArray())), String.Join("\r\n", quality.ToArray()));
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { timer.Dispose(); if (session != null) { session.Dispose(); session = null; } } base.Dispose(disposing); if (disposing && softwareIcon != null) { softwareIcon.Dispose(); softwareIcon = null; } }
    }
}
