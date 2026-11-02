using System;
using System.Drawing;
using System.Windows.Forms;

namespace ModbusSimulator
{
    /// <summary>独立折叠日志/发送；保存已展开日志高度，把释放的空间返还给上方数据视图。</summary>
    public sealed class TrafficWorkspace : SplitContainer
    {
        public readonly Button LogToggle = Ui.Button("▾ 通信日志", 100, false);
        public readonly Button RawToggle = Ui.Button("▾ 自定义发送", 124, false);
        private readonly TableLayoutPanel layout = new SmoothTable();
        private readonly Control logTools;
        private readonly TrafficLog log;
        private readonly RawSendPanel sender;
        private bool logCollapsed, rawCollapsed, arranging, ready;
        private int logHeight = 104;
        public bool LogCollapsed { get { return logCollapsed; } }
        public bool RawCollapsed { get { return rawCollapsed; } }
        public int ExpandedLogHeight { get { RememberLogHeight(); return logHeight; } }
        public void RestoreLayout(bool collapseLog, bool collapseRaw, int expandedLogHeight)
        {
            logCollapsed = collapseLog; rawCollapsed = collapseRaw;
            logHeight = Math.Max(45, Math.Min(4000, expandedLogHeight)); ApplySections();
        }
        private int LogHeaderHeight
        {
            get
            {
                int height = Math.Max(32, Font.Height + 15);
                // Form.Scale可先放大按钮边界，字体尚未变化；用真实按钮高度覆盖这段过程。
                if (logTools != null) foreach (Control action in logTools.Controls)
                    height = Math.Max(height, Math.Max(action.Height, action.PreferredSize.Height) + action.Margin.Vertical + logTools.Padding.Vertical);
                return Math.Max(height, LogToggle.PreferredSize.Height + LogToggle.Margin.Vertical);
            }
        }
        private int RawHeaderHeight { get { return Math.Max(Math.Max(29, Font.Height + 12), Math.Max(RawToggle.Height, RawToggle.PreferredSize.Height) + RawToggle.Margin.Vertical); } }
        private int SenderHeight { get { return Math.Max(128, sender.MinimumSize.Height); } }

        public TrafficWorkspace(Control data, TrafficLog trafficLog, RawSendPanel rawSender, Control tools)
        {
            DoubleBuffered = true; Dock = DockStyle.Fill; Margin = new Padding(0);
            Orientation = Orientation.Horizontal; Size = new Size(850, 700); SplitterWidth = 6;
            Panel1MinSize = 220; Panel2MinSize = 60; FixedPanel = FixedPanel.Panel2;
            log = trafficLog; sender = rawSender; logTools = tools;
            Panel1.Controls.Add(data);
            layout.Dock = DockStyle.Fill; layout.Margin = new Padding(0); layout.ColumnCount = 1; layout.RowCount = 4;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, LogHeaderHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, RawHeaderHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, SenderHeight));
            TableLayoutPanel logHeader = new SmoothTable { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 2, RowCount = 1 };
            logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            LogToggle.Dock = DockStyle.Fill; LogToggle.Margin = new Padding(0, 2, 4, 2); logHeader.Controls.Add(LogToggle, 0, 0);
            logTools.Dock = DockStyle.Fill; logTools.Margin = new Padding(0); logHeader.Controls.Add(logTools, 1, 0);
            FlowLayoutPanel rawHeader = new SmoothFlow { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            RawToggle.Margin = new Padding(0); rawHeader.Controls.Add(RawToggle);
            log.Dock = DockStyle.Fill; log.Margin = new Padding(0); sender.Dock = DockStyle.Fill; sender.Margin = new Padding(0);
            layout.Controls.Add(logHeader, 0, 0); layout.Controls.Add(log, 0, 1); layout.Controls.Add(rawHeader, 0, 2); layout.Controls.Add(sender, 0, 3);
            Panel2.Controls.Add(layout);
            LogToggle.Click += delegate { RememberLogHeight(); logCollapsed = !logCollapsed; ApplySections(); };
            RawToggle.Click += delegate { RememberLogHeight(); rawCollapsed = !rawCollapsed; ApplySections(); };
            sender.SizeChanged += delegate { if (ready && !arranging && !rawCollapsed) { RememberLogHeight(); ApplySections(); } };
            SizeChanged += delegate { if (ready && !arranging) ApplySections(); };
            FontChanged += delegate { if (ready && !arranging) ApplySections(); };
            EventHandler metricsChanged = delegate { if (ready && !arranging) { RememberLogHeight(); ApplySections(); } };
            foreach (Control action in logTools.Controls) action.SizeChanged += metricsChanged;
            RawToggle.SizeChanged += metricsChanged;
            SplitterMoved += delegate { if (ready && !arranging) RememberLogHeight(); };
            ready = true; ApplySections();
        }
        private void RememberLogHeight()
        { if (!logCollapsed && log.Visible && log.Height > 0) logHeight = log.Height; }
        private void ApplySections()
        {
            if (!ready || arranging || IsDisposed || Disposing) return;
            arranging = true; SuspendLayout(); layout.SuspendLayout();
            try
            {
                int headers = LogHeaderHeight + RawHeaderHeight;
                int rawHeight = rawCollapsed ? 0 : SenderHeight;
                int available = Math.Max(0, ClientSize.Height - SplitterWidth - Panel1MinSize);
                int minimum = headers + rawHeight + (logCollapsed ? 0 : 45);
                // 布局/缩放中可能短暂小于最小尺寸，先约束最小值再设分隔位置。
                Panel2MinSize = Math.Min(minimum, available);
                int wanted = headers + rawHeight + (logCollapsed ? 0 : logHeight);
                int lower = Math.Max(Panel2MinSize, Math.Min(wanted, available));
                SplitterDistance = Math.Max(Panel1MinSize, ClientSize.Height - SplitterWidth - lower);
                IsSplitterFixed = logCollapsed && rawCollapsed;
                layout.RowStyles[0].Height = LogHeaderHeight; layout.RowStyles[2].Height = RawHeaderHeight;
                layout.RowStyles[1].SizeType = logCollapsed ? SizeType.Absolute : SizeType.Percent;
                layout.RowStyles[1].Height = logCollapsed ? 0 : 100;
                layout.RowStyles[3].Height = rawHeight;
                log.Visible = logTools.Visible = !logCollapsed; sender.Visible = !rawCollapsed;
                LogToggle.Text = (logCollapsed ? "▸ " : "▾ ") + "通信日志";
                RawToggle.Text = (rawCollapsed ? "▸ " : "▾ ") + "自定义发送";
            }
            finally { layout.ResumeLayout(true); ResumeLayout(true); arranging = false; }
        }
    }
}
