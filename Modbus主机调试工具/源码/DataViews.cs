using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;

namespace ModbusMaster
{
    public sealed class DataViews : SmoothPanel
    {
        public readonly DataGridView ListGrid = AreaPanel.NewGrid();
        public readonly CardView Cards;
        private readonly AreaPanel[] areas;
        private readonly Func<RegisterBank> getBank;
        private readonly Form[] floats = new Form[4];
        private Control container;
        private long lastRevision = -1;
        private long cardsRevision = -1;
        private bool resizing;
        private readonly long[] areaRevisions = { -1, -1, -1, -1 };
        private readonly int[] order = { 2, 3, 0, 1 };
        public int CurrentMode { get; private set; }
        public DataViews(AreaPanel[] panels, Func<RegisterBank> bank)
        {
            areas = panels; getBank = bank; Dock = DockStyle.Fill; BackColor = Color.FromArgb(239, 244, 250);
            ListGrid.Columns.Add("Area", "数据区 / 功能码"); ListGrid.Columns.Add("Address", "协议地址"); ListGrid.Columns.Add("Reference", "参考编号");
            ListGrid.Columns.Add("Value", "数值 · 双击编辑"); ListGrid.Columns.Add("Hex", "HEX 原始值");
            ListGrid.Columns.Add("Parsed", "解析值"); ListGrid.Columns[5].ReadOnly = true; ListGrid.Columns[5].SortMode = DataGridViewColumnSortMode.NotSortable; ListGrid.Columns[5].MinimumWidth = 110;
            for (int i = 0; i < 5; i++) { ListGrid.Columns[i].ReadOnly = i != 3; ListGrid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; ListGrid.Columns[i].MinimumWidth = i == 0 ? 145 : 80; }
            ListGrid.Columns[0].FillWeight = 160; ListGrid.Columns[3].FillWeight = 120;
            AreaPanel.AddLabelColumns(ListGrid);
            ListGrid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (ListGrid.Disposing || ListGrid.IsDisposed) return;
                int area, address; if (!Map(e.RowIndex, out area, out address)) return;
                e.Value = e.ColumnIndex == 0 ? AreaPanel.Names[area] : e.ColumnIndex == 1 ? (object)address : e.ColumnIndex == 2 ? areas[area].Reference(address) :
                    e.ColumnIndex == 3 ? areas[area].DisplayValue(address) : e.ColumnIndex == 4 ? "0x" + getBank().Get(area, address).ToString("X4") :
                    e.ColumnIndex == 5 ? areas[area].ParsedValue(address) : e.ColumnIndex == 6 ? areas[area].Presentation.GetName(address) : areas[area].Presentation.GetUnit(address);
            };
            ListGrid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (ListGrid.Disposing || ListGrid.IsDisposed) return;
                int area, address;
                if (e.ColumnIndex == 6 && Map(e.RowIndex, out area, out address) && String.IsNullOrEmpty(Convert.ToString(e.Value)))
                { e.Value = AreaPanel.Names[area] + " · " + address; e.FormattingApplied = true; }
            };
            ListGrid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                if (ListGrid.Disposing || ListGrid.IsDisposed) return;
                int area, address;
                if (ListGrid.CurrentCell != null && ListGrid.CurrentCell.ColumnIndex == 6 && Map(ListGrid.CurrentCell.RowIndex, out area, out address))
                    e.Control.Text = areas[area].Presentation.GetName(address);
            };
            ListGrid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e)
            { int area, address; if (e.ColumnIndex == 3 && Map(e.RowIndex, out area, out address) && !areas[area].CanWrite) e.Cancel = true; };
            ListGrid.CellValuePushed += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                if (ListGrid.Disposing || ListGrid.IsDisposed) return;
                int area, address; if (!Map(e.RowIndex, out area, out address)) return;
                if (e.ColumnIndex == 3) areas[area].SetText(address, Convert.ToString(e.Value));
                else if (e.ColumnIndex == 6 || e.ColumnIndex == 7) areas[area].SetLabelText(address, e.ColumnIndex == 7, Convert.ToString(e.Value));
            };
            ListGrid.CellValidating += delegate(object sender, DataGridViewCellValidatingEventArgs e)
            {
                if (ListGrid.Disposing || ListGrid.IsDisposed) return;
                int area, address; ushort value;
                if (e.ColumnIndex == 3 && Map(e.RowIndex, out area, out address) && !areas[area].Parse(Convert.ToString(e.FormattedValue), out value))
                { e.Cancel = true; ListGrid.Rows[e.RowIndex].ErrorText = "无效数值；Esc 取消"; }
                if (e.RowIndex >= 0 && (e.ColumnIndex == 6 || e.ColumnIndex == 7))
                {
                    string error = AreaPanel.LabelTextError(Convert.ToString(e.FormattedValue), e.ColumnIndex == 7);
                    if (error != null) { e.Cancel = true; ListGrid.Rows[e.RowIndex].ErrorText = error; }
                }
            };
            ListGrid.CellEndEdit += delegate(object sender, DataGridViewCellEventArgs e) { if (!ListGrid.Disposing && !ListGrid.IsDisposed && e.RowIndex >= 0 && e.RowIndex < ListGrid.RowCount) ListGrid.Rows[e.RowIndex].ErrorText = ""; };
            ListGrid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                int area, address;
                if (e.ColumnIndex == 5 && Map(e.RowIndex, out area, out address) && area >= 2) areas[area].EditParseRule(address);
            };
            ContextMenuStrip itemMenu = new ContextMenuStrip();
            ToolStripMenuItem editRule = new ToolStripMenuItem("单项解析设置…"); itemMenu.Items.Add(editRule);
            int contextArea = -1, contextAddress = -1;
            ListGrid.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                contextArea = contextAddress = -1;
                DataGridView.HitTestInfo hit = ListGrid.HitTest(e.X, e.Y);
                int area, address;
                if (hit.ColumnIndex < 0 || !Map(hit.RowIndex, out area, out address) || area < 2 || !Commit()) return;
                ListGrid.CurrentCell = ListGrid[hit.ColumnIndex, hit.RowIndex]; contextArea = area; contextAddress = address;
            };
            itemMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            { e.Cancel = !IsWordAddress(contextArea, contextAddress); };
            editRule.Click += delegate
            { if (IsWordAddress(contextArea, contextAddress)) areas[contextArea].EditParseRule(contextAddress); };
            ListGrid.ContextMenuStrip = itemMenu;
            ListGrid.Disposed += delegate { itemMenu.Dispose(); };
            foreach (AreaPanel panel in areas)
            {
                AreaPanel selected = panel;
                panel.DefinitionChanged += Reload; panel.CommitOtherEdits = Commit;
                panel.DisplayFormatChanged += delegate { ListGrid.Invalidate(); };
                panel.LocateRequested = delegate(int address) { Locate(selected.Area, address); };
            }
            Cards = new CardView(areas, getBank);
            foreach (AreaPanel panel in areas) panel.DisplayFormatChanged += delegate { Cards.RefreshValues(); };
            SetMode(0);
        }
        private bool Map(int row, out int area, out int address)
        {
            RegisterBank bank = getBank();
            foreach (int candidate in order)
            {
                int count = bank.Count(candidate);
                if (row >= 0 && row < count) { area = candidate; address = bank.Start(candidate) + row; return true; }
                row -= count;
            }
            area = address = 0; return false;
        }
        private bool IsWordAddress(int area, int address)
        { return area >= 2 && area < areas.Length && address >= getBank().Start(area) && address < getBank().Start(area) + getBank().Count(area); }
        public bool Commit()
        {
            if (ListGrid.IsCurrentCellInEditMode && ListGrid.EditingControl != null && ListGrid.CurrentCell != null)
            {
                int area, address; ushort value;
                if (ListGrid.CurrentCell.ColumnIndex == 3 && Map(ListGrid.CurrentCell.RowIndex, out area, out address) && !areas[area].Parse(ListGrid.EditingControl.Text, out value))
                { ListGrid.CurrentRow.ErrorText = "无效数值；Esc 取消"; return false; }
                if (ListGrid.CurrentCell.ColumnIndex == 6 || ListGrid.CurrentCell.ColumnIndex == 7)
                {
                    string error = AreaPanel.LabelTextError(ListGrid.EditingControl.Text, ListGrid.CurrentCell.ColumnIndex == 7);
                    if (error != null) { ListGrid.CurrentRow.ErrorText = error; return false; }
                }
            }
            return ListGrid.EndEdit();
        }
        public void Reload()
        {
            lastRevision = -1;
            cardsRevision = -1;
            for (int area = 0; area < 4; area++) areaRevisions[area] = -1;
            ListGrid.CancelEdit(); ListGrid.CurrentCell = null;
            if (ListGrid.RowCount > 0 && ListGrid.Height > ListGrid.ColumnHeadersHeight + 4) ListGrid.FirstDisplayedScrollingRowIndex = 0;
            ListGrid.RowCount = 0;
            int count = 0; foreach (int area in order) count += getBank().Count(area);
            ListGrid.RowCount = count;
            if (Cards != null) Cards.Rebuild();
            // 定义变化只重建布局容器，先移出共享面板，避免容器释放时连带释放数据区。
            if ((CurrentMode == 1 || CurrentMode == 2) && container != null)
            {
                SuspendLayout();
                foreach (AreaPanel panel in areas) if (panel.Parent != null) panel.Parent.Controls.Remove(panel);
                Controls.Remove(container); container.Dispose(); container = BuildAreaLayout(CurrentMode); Controls.Add(container);
                ResumeLayout();
            }
            for (int area = 0; area < floats.Length; area++) if (getBank().Count(area) == 0) CloseFloatingWindow(area);
            if (CurrentMode == 3)
            {
                UpdateFloatGuide(); bool enabled = false, missing = false;
                foreach (int area in order) if (getBank().Count(area) > 0) { enabled = true; if (floats[area] == null) missing = true; }
                Form main = FindForm();
                if (!enabled && main != null && main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
                else if (missing) ShowFloats();
            }
        }
        private Control BuildAreaLayout(int mode)
        {
            List<int> enabled = new List<int>();
            foreach (int area in order) if (getBank().Count(area) > 0) enabled.Add(area);
            if (enabled.Count == 0) return EmptyAreaGuide();
            if (mode == 2)
            {
                TabControl tabs = new TabControl { Dock = DockStyle.Fill };
                foreach (int area in enabled)
                { TabPage tab = new TabPage(AreaPanel.Names[area]) { BackColor = Color.White }; tab.Controls.Add(areas[area]); tabs.TabPages.Add(tab); }
                return tabs;
            }
            int columns = enabled.Count == 1 ? 1 : 2, rows = (enabled.Count + columns - 1) / columns;
            TableLayoutPanel tiled = new SmoothTable { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows };
            for (int col = 0; col < columns; col++) tiled.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
            for (int row = 0; row < rows; row++) tiled.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
            for (int i = 0; i < enabled.Count; i++) tiled.Controls.Add(areas[enabled[i]], i % columns, i / columns);
            return tiled;
        }
        private static Control EmptyAreaGuide()
        { return new Label { Dock = DockStyle.Fill, Text = "无启用数据区；请在设置中将所需数据区数量设为大于 0。", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.SlateGray }; }
        private void UpdateFloatGuide()
        {
            if (container == null || container.Controls.Count == 0) return;
            int count = 0; foreach (int area in order) if (getBank().Count(area) > 0) count++;
            container.Controls[0].Text = count == 0 ? "无启用数据区；请在设置中将所需数据区数量设为大于 0。" :
                "已启用的数据区在独立窗口显示\r\n\r\n可拖动、缩放，放到其他显示器。\r\n关闭某个浮窗仅隐藏视图，不停止通信。\r\n点击“重新排列浮窗”可再次显示全部。";
        }
        private void CloseFloatingWindow(int area)
        {
            if (floats[area] == null) return;
            if (areas[area].Parent == floats[area]) floats[area].Controls.Remove(areas[area]);
            floats[area].Dispose(); floats[area] = null;
        }
        public void CloseFloatingWindows()
        { for (int area = 0; area < floats.Length; area++) CloseFloatingWindow(area); }
        public bool SetMode(int mode)
        {
            if (mode < 0 || mode > 4) throw new ArgumentOutOfRangeException("mode");
            if (!Commit()) return false;
            foreach (AreaPanel area in areas) if (!area.Commit()) return false;
            if (CurrentMode == 3 && mode != 3)
            {
                Form main = FindForm();
                if (main != null && main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
            }
            SuspendLayout();
            // 先移出数据视图再销毁容器，保证切换模式不销毁控件、数据或编辑格式。
            foreach (AreaPanel panel in areas) if (panel.Parent != null) panel.Parent.Controls.Remove(panel);
            if (ListGrid.Parent != null) ListGrid.Parent.Controls.Remove(ListGrid);
            if (Cards.Parent != null) Cards.Parent.Controls.Remove(Cards);
            CloseFloatingWindows();
            if (container != null) { Controls.Remove(container); container.Dispose(); }
            CurrentMode = mode;
            if (mode == 0)
            {
                Panel list = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.White };
                list.Controls.Add(ListGrid); container = list;
            }
            else if (mode == 1 || mode == 2) container = BuildAreaLayout(mode);
            else if (mode == 3)
            {
                Panel guide = new SmoothPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(30) };
                guide.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    Text = "各数据区已在独立窗口显示\r\n\r\n可拖动、缩放，放到其他显示器。\r\n关闭某个浮窗仅隐藏视图，不停止通信。\r\n点击“重新排列浮窗”可再次显示全部。", ForeColor = Color.SlateGray, Font = new Font("Microsoft YaHei UI", 12) });
                container = guide;
            }
            else
            { Panel cardHost = new SmoothPanel { Dock = DockStyle.Fill }; cardHost.Controls.Add(Cards); container = cardHost; }
            Controls.Add(container); container.Dock = DockStyle.Fill;
            if (ListGrid.RowCount == 0) Reload(); else { lastRevision = -1; cardsRevision = -1; }
            if (mode == 3) UpdateFloatGuide();
            ResumeLayout();
            if (mode == 3) ShowFloats(); return true;
        }
        public void ShowFloats()
        {
            if (CurrentMode != 3) return;
            Form owner = FindForm(); if (owner == null) return;
            Rectangle screen = Screen.FromControl(owner).WorkingArea;
            int width = Math.Min(650, screen.Width / 2), height = Math.Min(410, screen.Height / 2);
            int position = 0;
            for (int i = 0; i < 4; i++)
            {
                int area = order[i];
                if (getBank().Count(area) == 0) { CloseFloatingWindow(area); continue; }
                if (floats[area] == null)
                {
                    Form window = new Form { Text = AreaPanel.Names[area] + " · Modbus 主机", Font = owner.Font, MinimumSize = new Size(430, 240), StartPosition = FormStartPosition.Manual, ShowInTaskbar = true, TopMost = false };
                    window.Controls.Add(areas[area]); UiTheme.Apply(window); UiLanguage.Apply(window);
                    window.ResizeBegin += delegate { BeginInteractiveResize(); };
                    window.ResizeEnd += delegate { EndInteractiveResize(); };
                    window.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; ((Form)sender).Hide(); } };
                    floats[area] = window;
                }
                floats[area].Bounds = new Rectangle(screen.Left + (position % 2) * width, screen.Top + (position / 2) * height, width, height); position++;
                if (!floats[area].Visible) floats[area].Show();
            }
            UpdateFloatGuide();
            // 无 Owner 的普通浮窗不随主界面最小化，也不在切换到其他软件时强制置前。
            if (position > 0) owner.WindowState = FormWindowState.Minimized;
        }
        public void Locate(int area, int address)
        {
            if (area < 0 || area >= areas.Length) throw new ArgumentOutOfRangeException("area");
            if (getBank().Count(area) == 0) return;
            int row = address - getBank().Start(area);
            if (row < 0 || row >= getBank().Count(area)) throw new ArgumentOutOfRangeException("address");
            if (!Commit() || !areas[area].Commit()) return;
            if (CurrentMode == 4) { Cards.Locate(area, address); return; }
            if (CurrentMode == 0)
            {
                foreach (int candidate in order) { if (candidate == area) break; row += getBank().Count(candidate); }
                ListGrid.CurrentCell = ListGrid[3, row]; ListGrid.FirstDisplayedScrollingRowIndex = row; ListGrid.Focus();
            }
            else
            {
                if (CurrentMode == 2) ((TabControl)areas[area].Parent.Parent).SelectedTab = (TabPage)areas[area].Parent;
                if (CurrentMode == 3)
                { if (floats[area] == null) ShowFloats(); if (floats[area] == null) return; if (!floats[area].Visible) floats[area].Show(); floats[area].Activate(); }
                areas[area].Grid.CurrentCell = areas[area].Grid[2, row]; areas[area].Grid.FirstDisplayedScrollingRowIndex = row; areas[area].Grid.Focus();
            }
        }
        public void RefreshValues()
        {
            if (resizing) return;
            long current = getBank().Revision;
            if (CurrentMode == 4 && current != cardsRevision) { Cards.RefreshValues(); cardsRevision = current; }
            if (CurrentMode == 0 && current != lastRevision && !ListGrid.IsCurrentCellInEditMode)
            { ListGrid.Invalidate(); lastRevision = current; }
            // 每个视图独立追踪已刷新版本。任一浮窗编辑/隐藏，不阻止其他窗格更新。
            foreach (AreaPanel panel in areas)
                if (panel.Visible && !panel.Grid.IsCurrentCellInEditMode && areaRevisions[panel.Area] != current)
                { panel.RefreshValues(); areaRevisions[panel.Area] = current; }
        }
        public void BeginInteractiveResize()
        {
            resizing = true;
            ListGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            foreach (AreaPanel panel in areas) panel.Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        }
        public void EndInteractiveResize()
        {
            ListGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (AreaPanel panel in areas) panel.Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            resizing = false; RefreshValues(); Invalidate();
        }
        // .NET Framework Grid 在隐藏/横向滚动后直接清列可能访问已移除列；释放前先回收编辑与滚动状态。
        private static void PrepareGridDispose(DataGridView grid)
        {
            if (grid.IsDisposed || grid.Disposing) return;
            grid.CancelEdit(); grid.CurrentCell = null;
            if (grid.ColumnCount > 0) grid.HorizontalScrollingOffset = 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (AreaPanel panel in areas) if (panel.Parent != null) panel.Parent.Controls.Remove(panel);
                CloseFloatingWindows();
                foreach (AreaPanel panel in areas) { PrepareGridDispose(panel.Grid); panel.Dispose(); panel.Settings.Dispose(); }
                PrepareGridDispose(ListGrid); ListGrid.Dispose();
                Cards.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
