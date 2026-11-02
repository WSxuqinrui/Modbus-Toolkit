using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace ModbusMaster
{
    /// <summary>显式选目标并手动提交原始字节；发送成功只代表驱动接受或进入发送队列。</summary>
    public sealed class RawSendPanel : UserControl
    {
        public readonly ComboBox TargetCombo = new ComboBox();
        public readonly RadioButton HexRadio = new RadioButton(), AsciiRadio = new RadioButton();
        public readonly ComboBox ChecksumCombo = new ComboBox(), ByteOrderCombo = new ComboBox(), NewlineCombo = new ComboBox();
        public readonly TextBox InputBox = new TextBox();
        public readonly Button SendButton = new Button();
        public readonly Label StatusLabel = new Label();
        public readonly CheckBox CycleCheck = new CheckBox { Text = "循环发送" };
        public readonly NumericUpDown PeriodInput = new NumericUpDown { Minimum = 20, Maximum = 600000, Value = 1000 };

        public Func<string[]> GetTargets { get; set; }
        public Func<string, byte[], bool> SendRequested { get; set; }
        public Func<bool> IsSendBusy { get; set; }
        public bool IsCycling { get { return cycling; } }

        private readonly Panel options = new Panel(), targetGroup = new Panel(), formatGroup = new Panel();
        private readonly Label targetLabel = new Label();
        private readonly Panel cycleGroup = new Panel();
        private readonly Label periodUnit = new Label { Text = "ms", TextAlign = ContentAlignment.MiddleLeft };
        private readonly Timer cycleTimer = new Timer();
        private bool cycling, changingCycle;
        private string cycleTarget;
        private byte[] cyclePayload;
        private readonly ToolTip toolTip = new ToolTip();
        private readonly Font panelFont = new Font("Microsoft YaHei UI", 9), inputFont = new Font("Consolas", 9);
        private bool ready, refreshing, targetsAvailable, targetSelectionLost, showTargetChooser;

        public RawSendPanel()
        {
            SuspendLayout();
            Font = panelFont; AutoScaleMode = AutoScaleMode.Inherit;
            DoubleBuffered = true; BackColor = Color.FromArgb(247, 250, 253); Padding = new Padding(4);
            Size = new Size(540, 128); MinimumSize = new Size(300, 128);
            targetLabel.Text = "目标"; targetLabel.TextAlign = ContentAlignment.MiddleLeft;
            TargetCombo.AccessibleName = "原始发送目标"; TargetCombo.DropDownWidth = 330;
            targetGroup.Controls.Add(targetLabel); targetGroup.Controls.Add(TargetCombo);
            HexRadio.Text = "HEX"; AsciiRadio.Text = "ASCII";
            HexRadio.Checked = true; HexRadio.AutoSize = false; AsciiRadio.AutoSize = false;
            formatGroup.Controls.Add(HexRadio); formatGroup.Controls.Add(AsciiRadio);
            ConfigureCombo(TargetCombo, new string[0]);
            ConfigureCombo(ChecksumCombo, RawPayload.ChecksumNames);
            ConfigureCombo(ByteOrderCombo, new string[] { "低字节在前", "高字节在前" });
            ConfigureCombo(NewlineCombo, new string[] { "无换行", "CR", "LF", "CRLF" });
            ChecksumCombo.AccessibleName = "原始发送附加校验"; ByteOrderCombo.AccessibleName = "校验字节序"; NewlineCombo.AccessibleName = "发送末尾换行";
            ChecksumCombo.SelectedIndex = ByteOrderCombo.SelectedIndex = NewlineCombo.SelectedIndex = 0;
            ChecksumCombo.DropDownWidth = 245;
            options.Controls.Add(targetGroup); options.Controls.Add(formatGroup); options.Controls.Add(ChecksumCombo);
            options.Controls.Add(ByteOrderCombo); options.Controls.Add(NewlineCombo);
            cycleGroup.Controls.Add(CycleCheck); cycleGroup.Controls.Add(PeriodInput); cycleGroup.Controls.Add(periodUnit); options.Controls.Add(cycleGroup);

            InputBox.Multiline = true; InputBox.AcceptsReturn = true; InputBox.AcceptsTab = false;
            InputBox.ScrollBars = ScrollBars.Vertical; InputBox.Font = inputFont; InputBox.MaxLength = 131072;
            InputBox.AccessibleName = "自定义原始报文输入"; InputBox.WordWrap = true;
            SendButton.Text = "发送"; SendButton.UseVisualStyleBackColor = false;
            SendButton.BackColor = Color.FromArgb(34, 103, 190); SendButton.ForeColor = Color.White;
            SendButton.FlatStyle = FlatStyle.Flat; SendButton.FlatAppearance.BorderSize = 0;
            StatusLabel.AutoEllipsis = true; StatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            StatusLabel.AccessibleName = "原始发送状态及字节预览";
            Controls.Add(options); Controls.Add(InputBox); Controls.Add(SendButton); Controls.Add(StatusLabel);
            const string pipeline = "输入原始字节→校验→换行，不再自动包Modbus。标准 Modbus TCP 通常选无校验。";
            toolTip.SetToolTip(targetLabel, pipeline); toolTip.SetToolTip(InputBox, pipeline);
            toolTip.SetToolTip(ChecksumCombo, pipeline);
            toolTip.SetToolTip(ByteOrderCombo, "多字节校验值的附加顺序，包括CRC16、CRC32及SUM16。");
            toolTip.SetToolTip(CycleCheck, "立即发送一次，再按周期发送。通道忙时跳过；连接/目标改变或发送失败时停止，重新打开软件不会自动发送。");
            toolTip.SetToolTip(PeriodInput, "循环周期20～600000ms；实际发送受通道及响应时间限制，不积压定时任务。");
            toolTip.SetToolTip(TargetCombo, "仅向当前选中的目标发送；多个目标必须手动选择。");
            toolTip.SetToolTip(AsciiRadio, "严格 ASCII 0～127，保留实际换行，不解释 \\r、\\n。");
            toolTip.SetToolTip(SendButton, "手动发送当前编排结果；已提交不代表对方已收到。");
            InputBox.TextChanged += PreviewChanged;
            HexRadio.CheckedChanged += PreviewChanged; AsciiRadio.CheckedChanged += PreviewChanged;
            ChecksumCombo.SelectedIndexChanged += PreviewChanged;
            ByteOrderCombo.SelectedIndexChanged += PreviewChanged; NewlineCombo.SelectedIndexChanged += PreviewChanged;
            TargetCombo.SelectedIndexChanged += PreviewChanged;
            SendButton.Click += Submit;
            CycleCheck.CheckedChanged += delegate { if (!ready || changingCycle) return; if (CycleCheck.Checked) StartCycle(); else StopCycle("已停止循环发送。"); };
            CycleCheck.TextChanged += delegate { if (ready) PerformLayout(); };
            cycleTimer.Tick += delegate { if (cycling) SendOnce(true); };
            ready = true; UpdateTargetVisibility(); ResumeLayout(true); UpdatePreview();
        }

        /// <summary>保留仍存在的目标；初次出现唯一目标可自动选中，原选择失效后须手动重选。</summary>
        public void RefreshTargets()
        {
            if (IsDisposed) return;
            string previous = TargetCombo.SelectedItem as string;
            bool changed = !targetsAvailable;
            refreshing = true; TargetCombo.BeginUpdate();
            try
            {
                string[] targets = GetTargets == null ? new string[0] : GetTargets() ?? new string[0];
                HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
                List<string> available = new List<string>();
                foreach (string target in targets)
                    if (!string.IsNullOrEmpty(target) && added.Add(target)) available.Add(target);
                if (TargetCombo.Items.Count != available.Count) changed = true;
                else for (int i = 0; i < available.Count; i++)
                    if (!string.Equals(TargetCombo.Items[i] as string, available[i], StringComparison.Ordinal)) { changed = true; break; }
                // 相同列表不重建下拉项，避免周期刷新打断用户操作或覆盖刚显示的发送结果。
                if (changed)
                {
                    TargetCombo.Items.Clear(); TargetCombo.Items.AddRange(available.ToArray());
                    TargetCombo.SelectedIndex = previous == null ? -1 : TargetCombo.Items.IndexOf(previous);
                    if (previous != null && TargetCombo.SelectedIndex < 0) targetSelectionLost = true;
                    // 原目标消失时保持未选状态，禁止把本次点击静默转发给剩余目标。
                    if (previous == null && !targetSelectionLost && TargetCombo.Items.Count == 1) TargetCombo.SelectedIndex = 0;
                }
                targetsAvailable = true;
            }
            catch (Exception error)
            {
                if (previous != null) targetSelectionLost = true;
                TargetCombo.Items.Clear(); targetsAvailable = false;
                ShowStatus("目标刷新失败：" + error.Message, true);
            }
            finally { TargetCombo.EndUpdate(); refreshing = false; UpdateTargetVisibility(); }
            if (targetsAvailable && changed) UpdatePreview();
        }

        private static void ConfigureCombo(ComboBox combo, string[] names)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.IntegralHeight = false; combo.MaxDropDownItems = 10;
            combo.Items.AddRange(names);
        }

        private void PreviewChanged(object sender, EventArgs args)
        {
            if (!refreshing && sender == TargetCombo && TargetCombo.SelectedIndex >= 0) targetSelectionLost = false;
            if (ready && !refreshing && sender == TargetCombo) UpdateTargetVisibility();
            if (ready && !refreshing) UpdatePreview();
        }
        private void UpdateTargetVisibility()
        {
            bool show = TargetCombo.Items.Count > 1 || (TargetCombo.Items.Count == 1 && TargetCombo.SelectedIndex < 0);
            bool changed = show != showTargetChooser; showTargetChooser = show;
            targetGroup.Visible = show;
            if (changed && ready) PerformLayout();
        }

        private byte[] Compose()
        {
            return RawPayload.Compose(InputBox.Text, HexRadio.Checked, ChecksumCombo.SelectedIndex,
                ByteOrderCombo.SelectedIndex == 0, NewlineCombo.SelectedIndex);
        }

        private void UpdatePreview()
        {
            ByteOrderCombo.Enabled = !cycling && RawPayload.ChecksumLength(ChecksumCombo.SelectedIndex) > 1;
            try
            {
                byte[] bytes = Compose();
                bool missingTarget = TargetCombo.Items.Count > 0 && TargetCombo.SelectedIndex < 0;
                string targetPrompt = targetSelectionLost ? "原目标已失效，请重新选择发送目标 · " : "请选择发送目标 · ";
                ShowStatus((missingTarget ? targetPrompt : "") + bytes.Length + " 字节  |  " + Preview(bytes), missingTarget);
            }
            catch (ArgumentException error) { ShowStatus(error.Message.Split('\r', '\n')[0], true); }
        }

        private static string Preview(byte[] bytes)
        {
            StringBuilder text = new StringBuilder();
            int count = Math.Min(40, bytes.Length);
            for (int i = 0; i < count; i++) { if (i != 0) text.Append(' '); text.Append(bytes[i].ToString("X2")); }
            if (count < bytes.Length) text.Append(" …");
            return text.ToString();
        }

        private void Submit(object sender, EventArgs args)
        { if (!cycling) SendOnce(false); }
        private bool SendOnce(bool automatic)
        {
            // 每次点击重新核对目标列表，避免连接变化后沿用已经失效的目标。
            RefreshTargets();
            if (!targetsAvailable) { if (automatic) StopCycle("循环发送已停止：目标不可用。"); return false; }
            try
            {
                byte[] payload = automatic ? cyclePayload : Compose();
                string target = TargetCombo.SelectedItem as string;
                if (automatic && target != cycleTarget) { StopCycle("循环发送已停止：连接或目标已改变，请重新选择。"); return false; }
                if (string.IsNullOrEmpty(target))
                {
                    ShowStatus(TargetCombo.Items.Count == 0 ? "暂无可发送目标，请先建立连接。" :
                        targetSelectionLost ? "原目标已失效，请重新选择发送目标。" : "请选择发送目标。", true);
                    if (automatic) StopCycle("循环发送已停止：没有可用目标。"); return false;
                }
                if (SendRequested == null) { ShowStatus("发送接口尚未就绪。", true); if (automatic) StopCycle("循环发送已停止：发送接口未就绪。"); return false; }
                if (automatic && IsSendBusy != null && IsSendBusy()) return true;
                SendButton.Enabled = false;
                bool accepted = SendRequested(target, (byte[])payload.Clone());
                ShowStatus(accepted ? "已提交发送 · " + payload.Length + " 字节  |  " + Preview(payload) : "发送未提交，请检查连接或通信日志。", !accepted);
                if (!accepted && automatic) StopCycle("循环发送已停止：发送未提交，请检查日志。");
                return accepted;
            }
            catch (ArgumentException error) { if (automatic) StopCycle("循环发送已停止：" + error.Message.Split('\r', '\n')[0]); else ShowStatus(error.Message.Split('\r', '\n')[0], true); return false; }
            catch (Exception error) { if (automatic) StopCycle("循环发送已停止：" + error.Message); else ShowStatus("发送失败：" + error.Message, true); return false; }
            finally { if (!IsDisposed) SendButton.Enabled = !cycling; }
        }
        private void StartCycle()
        {
            try
            {
                RefreshTargets(); cycleTarget = TargetCombo.SelectedItem as string;
                if (!Enabled || !targetsAvailable || String.IsNullOrEmpty(cycleTarget)) throw new ArgumentException("请先连接并选择有效发送目标。");
                cyclePayload = Compose(); cycling = true; cycleTimer.Interval = (int)PeriodInput.Value; ApplyCycleEditors();
                if (SendOnce(true) && cycling) cycleTimer.Start();
            }
            catch (Exception error) { StopCycle("循环发送未启动：" + error.Message.Split('\r', '\n')[0]); }
        }
        public void StopCycle(string reason)
        {
            cycleTimer.Stop(); cycling = false; cycleTarget = null; cyclePayload = null;
            changingCycle = true; try { CycleCheck.Checked = false; } finally { changingCycle = false; }
            ApplyCycleEditors(); if (!String.IsNullOrEmpty(reason)) ShowStatus(reason, !reason.StartsWith("已停止", StringComparison.Ordinal));
        }
        private void ApplyCycleEditors()
        {
            InputBox.ReadOnly = cycling; targetGroup.Enabled = formatGroup.Enabled = ChecksumCombo.Enabled = NewlineCombo.Enabled = PeriodInput.Enabled = !cycling;
            SendButton.Enabled = !cycling; UpdatePreview();
        }
        protected override void OnEnabledChanged(EventArgs e)
        { base.OnEnabledChanged(e); if (ready && !Enabled && cycling) StopCycle("循环发送已停止：连接已断开。"); }

        private void ShowStatus(string text, bool error)
        {
            StatusLabel.Text = text; StatusLabel.ForeColor = error ? UiTheme.ErrorText : UiTheme.MutedText;
            toolTip.SetToolTip(StatusLabel, text);
        }

        protected override void OnLayout(LayoutEventArgs args)
        {
            base.OnLayout(args);
            if (!ready) return;
            int gap = 4, rowGap = 2, line = Math.Max(24, ChecksumCombo.PreferredHeight + 1);
            int width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            int labelWidth = TextRenderer.MeasureText(targetLabel.Text, Font).Width + 3;
            targetLabel.SetBounds(0, 0, labelWidth, line);
            TargetCombo.SetBounds(labelWidth, 0, 142, line);
            targetGroup.Size = new Size(labelWidth + 142, line);
            int hexWidth = TextRenderer.MeasureText("HEX", Font).Width + 20;
            int asciiWidth = TextRenderer.MeasureText("ASCII", Font).Width + 20;
            HexRadio.SetBounds(0, 0, hexWidth, line); AsciiRadio.SetBounds(hexWidth, 0, asciiWidth, line);
            formatGroup.Size = new Size(hexWidth + asciiWidth, line);
            ChecksumCombo.Width = Math.Max(180, TextRenderer.MeasureText("CRC-16/CCITT-FALSE", Font).Width + 28);
            ByteOrderCombo.Width = Math.Max(110, TextRenderer.MeasureText("高字节在前", Font).Width + 28);
            NewlineCombo.Width = Math.Max(84, TextRenderer.MeasureText("无换行", Font).Width + 28);
            int cycleWidth = TextRenderer.MeasureText(CycleCheck.Text, Font).Width + 24;
            CycleCheck.SetBounds(0, 0, cycleWidth, line); PeriodInput.SetBounds(cycleWidth, 0, 88, line);
            periodUnit.SetBounds(PeriodInput.Right + 3, 0, Math.Max(32, TextRenderer.MeasureText(periodUnit.Text, Font).Width + 6), line); cycleGroup.Size = new Size(periodUnit.Right, line);
            int x = 0, y = 0;
            foreach (Control control in new Control[] { targetGroup, formatGroup, ChecksumCombo, ByteOrderCombo, NewlineCombo, cycleGroup })
            {
                if (control == targetGroup && !showTargetChooser) continue;
                if (x != 0 && x + control.Width > width) { x = 0; y += line + rowGap; }
                control.SetBounds(x, y, Math.Min(control.Width, width), line); x += control.Width + gap;
            }
            int optionsHeight = y + line;
            options.SetBounds(Padding.Left, Padding.Top, width, optionsHeight);
            int statusHeight = Math.Max(18, Font.Height + 2), editTop = options.Bottom + 3;
            int minimumHeight = Padding.Vertical + optionsHeight + 3 + 40 + 3 + statusHeight;
            // 放大字体时保留完整输入区和发送按钮；容器可按 MinimumSize.Height 预留空间。
            if (MinimumSize.Height != Math.Max(128, minimumHeight)) MinimumSize = new Size(300, Math.Max(128, minimumHeight));
            int statusTop = ClientSize.Height - Padding.Bottom - statusHeight;
            int editHeight = Math.Max(40, statusTop - 3 - editTop);
            int buttonWidth = Math.Max(78, TextRenderer.MeasureText(SendButton.Text, Font).Width + 28);
            InputBox.SetBounds(Padding.Left, editTop, Math.Max(1, width - buttonWidth - gap), editHeight);
            SendButton.SetBounds(InputBox.Right + gap, editTop, buttonWidth, editHeight);
            StatusLabel.SetBounds(Padding.Left, statusTop, width, statusHeight);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { cycling = false; cycleTimer.Stop(); cycleTimer.Dispose(); toolTip.Dispose(); }
            base.Dispose(disposing);
            if (disposing) { panelFont.Dispose(); inputFont.Dispose(); }
        }
    }
}
