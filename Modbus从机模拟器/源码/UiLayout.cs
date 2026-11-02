using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;

namespace ModbusSimulator
{
    public sealed partial class MainForm
    {
        public DataViews Views;
        private readonly ComboBox displayMode = new ComboBox(), selectedArea = new ComboBox();
        private readonly Panel areaSettings = new SmoothPanel();
        public SplitContainer BodySplit;
        public Button SidebarToggle;
        public Button LogToggle, RawSendToggle;
        private TrafficWorkspace trafficWorkspace;
        private Action<bool> setSidebarCollapsed;
        private readonly ComboBox languageMode = new ComboBox(), themeMode = new ComboBox();
        private readonly ToolTip projectLinkHint = new ToolTip();
        private void BuildMainUi(out Button save, out Button refreshCom, out Button clear, out Button export)
        {
            Text = "Modbus 从机模拟器 2.9";
            Font = new Font("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(239, 244, 250); ForeColor = Color.FromArgb(37, 55, 78);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(980, 720);
            Rectangle available = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1240, available.Width - 60), Math.Min(860, available.Height - 90)); StartPosition = FormStartPosition.CenterScreen;
            TableLayoutPanel root = new SmoothTable { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 14, 7), ColumnCount = 1, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); Controls.Add(root);
            SplitContainer body = new SmoothSplit { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, Size = new Size(1180, 740), SplitterDistance = 310, Panel1MinSize = 280, Panel2MinSize = 540 };
            BodySplit = body;
            Panel scroll = new SmoothPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(10) };
            TableLayoutPanel sidebar = new SmoothTable { Dock = DockStyle.Top, Height = 1033, ColumnCount = 1, RowCount = 10, Padding = new Padding(0, 0, 4, 0) };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int size in new int[] { 28, 282, 63, 34, 33, 345, 60, 35, 34, 100 }) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
            TableLayoutPanel connectionHeader = new SmoothTable { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 3, RowCount = 1 };
            connectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); connectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72)); connectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            connectionHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            connectionHeader.Controls.Add(Section("连接设置"), 0, 0);
            save = Ui.Button("保存配置", 72, false); save.Dock = load.Dock = DockStyle.Fill; save.Margin = load.Margin = new Padding(1, 0, 0, 0);
            connectionHeader.Controls.Add(load, 1, 0); connectionHeader.Controls.Add(save, 2, 0); sidebar.Controls.Add(connectionHeader, 0, 0);
            settings.Dock = DockStyle.Fill; settings.Margin = new Padding(0);
            TableLayoutPanel conn = new SmoothTable { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9 };
            conn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78)); conn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 9; i++) conn.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            ConfigureCombo(mode, TransportCatalog.Names); mode.SelectedIndex = TransportCatalog.IndexOf(0);
            mode.DropDownWidth = 260;
            ConfigureCombo(com, new string[0]); com.DropDownStyle = ComboBoxStyle.DropDown;
            ConfigureCombo(baud, new string[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" }); baud.SelectedItem = "9600";
            ConfigureCombo(parity, new string[] { "None", "Even", "Odd" }); ConfigureCombo(stopBits, new string[] { "1", "2" });
            ConfigureCombo(dataBits, new string[] { "8", "7" });
            ip.Text = "0.0.0.0"; ip.Dock = DockStyle.Fill; ip.AccessibleName = "监听IP";
            port.Minimum = 1; port.Maximum = 65535; port.Value = 502; port.Dock = DockStyle.Fill;
            unit.Minimum = 1; unit.Maximum = 247; unit.Value = 1; unit.Dock = DockStyle.Fill;
            Panel endpoint = new SmoothPanel { Dock = DockStyle.Fill }; endpoint.Controls.Add(ip); endpoint.Controls.Add(com);
            AreaPanel.Setting(conn, 0, "通信模式", mode); AreaPanel.Setting(conn, 1, "IP / COM", endpoint); AreaPanel.Setting(conn, 2, "网络端口", port);
            AreaPanel.Setting(conn, 3, "站号", unit); AreaPanel.Setting(conn, 4, "波特率", baud); AreaPanel.Setting(conn, 5, "校验", parity); AreaPanel.Setting(conn, 6, "停止位", stopBits);
            AreaPanel.Setting(conn, 7, "数据位", dataBits);
            refreshCom = Ui.Button("刷新串口列表", 155, false); refreshCom.Dock = DockStyle.Fill; conn.Controls.Add(refreshCom, 0, 8); conn.SetColumnSpan(refreshCom, 2);
            settings.Controls.Add(conn); sidebar.Controls.Add(settings, 0, 1);
            TableLayoutPanel service = new SmoothTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            service.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); service.RowStyles.Add(new RowStyle(SizeType.Absolute, 31)); service.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            toggle.Dock = DockStyle.Fill; state.Dock = DockStyle.Fill; state.Text = "● 已停止"; state.TextAlign = ContentAlignment.MiddleLeft;
            service.Controls.Add(toggle, 0, 0); service.Controls.Add(state, 0, 1); sidebar.Controls.Add(service, 0, 2);
            sidebar.Controls.Add(Section("数据区设置"), 0, 3);
            ConfigureCombo(selectedArea, new string[] { "保持寄存器 · 03 / 06 / 10", "输入寄存器 · 04", "线圈 · 01 / 05 / 0F", "离散输入 · 02" });
            sidebar.Controls.Add(selectedArea, 0, 4);
            for (int area = 0; area < 4; area++) Areas[area] = new AreaPanel(bank, area, AddLog);
            areaSettings.Dock = DockStyle.Fill; areaSettings.Margin = new Padding(0); sidebar.Controls.Add(areaSettings, 0, 5);
            tip.Dock = DockStyle.Fill; tip.ForeColor = Color.SlateGray; sidebar.Controls.Add(tip, 0, 6);
            sidebar.Controls.Add(new Label { Text = "地址从 0 开始；修改后应用定义。", Dock = DockStyle.Fill, ForeColor = Color.SlateGray }, 0, 7);
            Label systemHeader = Section("系统设置"); systemHeader.Name = "systemSettingsHeader";
            sidebar.Controls.Add(systemHeader, 0, 8); sidebar.Controls.Add(BuildSystemSettings(), 0, 9);
            SidebarLayout.Normalize(conn, true); SidebarLayout.Normalize(service, false); SidebarLayout.Normalize(sidebar, false);
            scroll.Controls.Add(sidebar); body.Panel1.Controls.Add(scroll);

            TableLayoutPanel work = new SmoothTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            work.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); work.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); work.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel tools = new SmoothFlow { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
            SidebarToggle = Ui.Button("收起设置", 82, false); tools.Controls.Add(SidebarToggle);
            FlowLayoutPanel fileTools = new SmoothFlow { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            Button saveButton = save;
            setSidebarCollapsed = delegate(bool collapsed) {
                body.Panel1Collapsed = collapsed; SidebarToggle.Text = collapsed ? "展开设置" : "收起设置";
                if (collapsed && load.Parent != fileTools) { fileTools.Controls.Add(load); fileTools.Controls.Add(saveButton); tools.Controls.Add(fileTools); }
                else if (!collapsed && load.Parent != connectionHeader) { tools.Controls.Remove(fileTools); connectionHeader.Controls.Add(load, 1, 0); connectionHeader.Controls.Add(saveButton, 2, 0); }
            };
            SidebarToggle.Click += delegate { setSidebarCollapsed(!body.Panel1Collapsed); };
            tools.Controls.Add(new Label { Text = "数据显示", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 10, 0) });
            ConfigureCombo(displayMode, new string[] { "列表", "平铺窗格", "选项卡", "独立浮窗", "逐项卡片" }); displayMode.Dock = DockStyle.None; displayMode.Width = 120; tools.Controls.Add(displayMode);
            Button restoreFloats = Ui.Button("重新排列浮窗", 120, false); restoreFloats.Visible = false; tools.Controls.Add(restoreFloats);
            counts.AutoSize = true; counts.Margin = new Padding(5, 6, 0, 0); tools.Controls.Add(counts);
            Views = new DataViews(Areas, delegate { return bank; });
            FlowLayoutPanel logTools = new SmoothFlow { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            pause.Margin = new Padding(0, 6, 10, 0); logTools.Controls.Add(pause);
            clear = Ui.Button("清空", 60, false); export = Ui.Button("导出日志", 90, false); logTools.Controls.Add(clear); logTools.Controls.Add(export);
            TrafficWorkspace workspace = new TrafficWorkspace(Views, logs, rawSender, logTools);
            trafficWorkspace = workspace; LogToggle = workspace.LogToggle; RawSendToggle = workspace.RawToggle;
            work.Controls.Add(tools, 0, 0); work.Controls.Add(workspace, 0, 1); body.Panel2.Controls.Add(work); root.Controls.Add(body, 0, 0);
            notice.SetStatus("四区响应 · Enter 提交", "四区同时响应 · 按 Enter 提交 · HEX 加 0x · 设置区可滚动，日志分隔条可拖动");
            notice.Dock = DockStyle.Fill; notice.ForeColor = Color.SlateGray; notice.TextAlign = ContentAlignment.MiddleLeft; TableLayoutPanel footer = new SmoothTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); notice.Margin = new Padding(0, 0, 6, 0); footer.Controls.Add(notice, 0, 0);
            root.Controls.Add(footer, 0, 1);
            languageMode.SelectedIndexChanged += delegate { ApplyUiAppearance(); }; themeMode.SelectedIndexChanged += delegate { ApplyUiAppearance(); };
            selectedArea.SelectedIndexChanged += delegate { ShowSelectedSettings(); };
            displayMode.SelectedIndexChanged += delegate
            { if (!Views.SetMode(displayMode.SelectedIndex)) displayMode.SelectedIndex = Views.CurrentMode; restoreFloats.Visible = Views.CurrentMode == 3; };
            restoreFloats.Click += delegate { displayMode.SelectedIndex = 3; Views.ShowFloats(); };
            FormClosed += delegate { Views.CloseFloatingWindows(); };
            ShowSelectedSettings();
        }
        // 与连接区共用90px标签列，保持窄侧栏与DPI缩放下的输入边界一致。
        private TableLayoutPanel BuildSystemSettings()
        {
            TableLayoutPanel system = new SmoothTable { Name = "systemSettings", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            system.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); system.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 31, 31, 38 }) system.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            ConfigureCombo(languageMode, new string[] { "中文", "English", "日本語", "한국어", "Русский" });
            languageMode.Name = "languageMode"; languageMode.AccessibleName = "界面语言";
            ConfigureCombo(themeMode, new string[] { "明亮模式", "暗黑模式" });
            themeMode.Name = "themeMode"; themeMode.AccessibleName = "界面主题";
            AreaPanel.Setting(system, 0, "语言", languageMode); AreaPanel.Setting(system, 1, "主题", themeMode);
            LinkLabel project = new LinkLabel { Name = "projectLink", Text = ProjectInfo.RepositoryName, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, TabStop = true,
                AccessibleName = "GitHub 项目地址", AccessibleDescription = ProjectInfo.RepositoryUrl };
            project.Links.Clear(); project.Links.Add(0, project.Text.Length, ProjectInfo.RepositoryUrl);
            projectLinkHint.SetToolTip(project, ProjectInfo.RepositoryUrl);
            // 原生LinkLabel独立保存链接颜色，随主题的ForeColor变化同步亮度。
            EventHandler updateLinkColor = delegate {
                project.LinkColor = UiTheme.IsDark ? Color.FromArgb(116, 185, 255) : Color.FromArgb(28, 101, 184);
                project.ActiveLinkColor = UiTheme.IsDark ? Color.FromArgb(167, 213, 255) : Color.FromArgb(17, 73, 143);
                project.VisitedLinkColor = project.LinkColor;
            };
            project.ForeColorChanged += updateLinkColor; updateLinkColor(project, EventArgs.Empty);
            // 只有用户点击才启动系统默认浏览器；启动失败时保留窗口与连接状态。
            project.LinkClicked += delegate {
                try { Process.Start(new ProcessStartInfo(ProjectInfo.RepositoryUrl) { UseShellExecute = true }); project.LinkVisited = true; }
                catch (System.ComponentModel.Win32Exception error) { LocalizedMessageBox.Show(this, "无法打开项目地址：" + error.Message); }
                catch (InvalidOperationException error) { LocalizedMessageBox.Show(this, "无法打开项目地址：" + error.Message); }
                catch (System.Security.SecurityException error) { LocalizedMessageBox.Show(this, "无法打开项目地址：" + error.Message); }
            };
            AreaPanel.Setting(system, 2, "项目地址", project);
            system.GetControlFromPosition(0, 2).Name = "projectAddressLabel";
            SidebarLayout.Normalize(system, true); Disposed += delegate { projectLinkHint.Dispose(); };
            return system;
        }
        private static Label Section(string text)
        { return new Label { Text = text, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }; }
        private void ShowSelectedSettings()
        {
            int[] order = { 2, 3, 0, 1 }; int area = order[Math.Max(0, selectedArea.SelectedIndex)];
            areaSettings.Controls.Clear(); areaSettings.Controls.Add(Areas[area].Settings);
            TableLayoutPanel connectionFields = mode.Parent as TableLayoutPanel;
            if (connectionFields != null) SidebarLayout.MatchFieldScale(Areas[area].Settings, connectionFields.ColumnStyles[0].Width);
        }
    }
}
