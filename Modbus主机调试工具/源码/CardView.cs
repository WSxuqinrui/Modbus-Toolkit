using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ModbusMaster
{
    // 卡片保持固定尺寸；编辑控件显示在下方共用区域，避免展开导致其它卡片跳动。
    internal sealed class CardDraft
    {
        public string Value, Name, Unit;
        public bool ValueDirty, NameDirty, UnitDirty;
    }
    internal sealed class CardValueLabel : Label
    {
        public CardValueLabel() { SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true); }
    }
    public sealed class AddressCard : Panel
    {
        private readonly RegisterBank bank;
        private readonly AreaPanel area;
        public readonly int Address;
        public readonly Button Header = Ui.Button("", 280, false);
        public readonly PillSwitch Switch = new PillSwitch();
        public readonly TextBox ValueInput = new TextBox(), NameInput = new TextBox(), UnitInput = new TextBox();
        public readonly Label ValueLabel = new CardValueLabel();
        public readonly TextBox QuickValueInput = new TextBox();
        private readonly ToolTip information = new ToolTip { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 15000, ShowAlways = true };
        public readonly Panel Editor = new SmoothPanel();
        public readonly Label DragHandle = new Label();
        private readonly Label detail = new Label(), error = new Label();
        private readonly StateLamp lamp = new StateLamp();
        private Button writeButton;
        private bool updating, dirty, opened, nameDirty, unitDirty, editorBuilt, quickValueInvalid;
        public event Action<AddressCard> EditorRequested;
        public int AreaId { get { return area.Area; } }
        public bool IsExpanded { get { return opened; } }
        public AddressCard(RegisterBank model, AreaPanel panel, int address)
        {
            bank = model; area = panel; Address = address; Width = 280; Height = 104; Margin = new Padding(6); Padding = new Padding(10);
            BackColor = Color.White; BorderStyle = BorderStyle.FixedSingle; DoubleBuffered = true;
            Header.SetBounds(10, 9, 226, 30); Header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Header.TextAlign = ContentAlignment.MiddleLeft;
            DragHandle.Text = "⠿"; DragHandle.TextAlign = ContentAlignment.MiddleCenter; DragHandle.SetBounds(Width - 37, 9, 25, 30);
            DragHandle.Anchor = AnchorStyles.Top | AnchorStyles.Right; DragHandle.Cursor = Cursors.SizeAll; DragHandle.ForeColor = Color.SlateGray;
            DragHandle.AccessibleName = "拖拽此处调整卡片顺序";
            ValueLabel.SetBounds(12, 45, 250, 35); ValueLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            ValueLabel.Font = new Font("Segoe UI", 18, FontStyle.Bold); ValueLabel.ForeColor = Color.FromArgb(26, 65, 108); ValueLabel.AutoEllipsis = true;
            detail.Visible = false;
            detail.SetBounds(12, 87, 250, 43); detail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            detail.ForeColor = Color.SlateGray; detail.AutoEllipsis = true;
            lamp.SetBounds(19, 49, 32, 32); lamp.Visible = area.Area < 2;
            Switch.SetBounds(77, 50, 90, 30); Switch.Visible = area.Area < 2; Switch.AccessibleName = AreaPanel.Names[area.Area] + " " + address;
            Switch.CheckedChanged += delegate
            {
                if (updating) return; ushort desired = Switch.Checked ? (ushort)1 : (ushort)0;
                updating = true; Switch.Checked = bank.Get(area.Area, Address) != 0; updating = false;
                try { area.SendWrite(Address, new ushort[] { desired }, false); }
                catch (ArgumentException ex) { error.Text = ex.Message; }
            };
            ValueLabel.Visible = area.Area >= 2;
            Controls.AddRange(new Control[] { ValueLabel, detail, lamp, Switch, Header, DragHandle });
            QuickValueInput.Name = "QuickValueEditor"; QuickValueInput.Visible = false; QuickValueInput.MaxLength = 64;
            QuickValueInput.Font = ValueLabel.Font; QuickValueInput.SetBounds(12, 45, 250, 35); QuickValueInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(QuickValueInput); QuickValueInput.BringToFront();
            ValueLabel.DoubleClick += delegate { BeginValueEdit(); };
            QuickValueInput.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { CommitValueEdit(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { CancelValueEdit(); e.SuppressKeyPress = true; }
            };
            QuickValueInput.Leave += delegate { CancelValueEdit(); };
            Header.Click += delegate { ToggleExpanded(); };
            RefreshValue();
        }
        internal void EnsureEditor()
        {
            if (editorBuilt) return; editorBuilt = true; BuildEditor(); RefreshValue();
        }
        private void BuildEditor()
        {
            Editor.Dock = DockStyle.Fill; Editor.BackColor = Color.White; Editor.Padding = new Padding(8);
            TableLayoutPanel table = new SmoothTable { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 4, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 31)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            NameInput.MaxLength = 64; UnitInput.MaxLength = 32;
            NameInput.Text = area.Presentation.GetName(Address); UnitInput.Text = area.Presentation.GetUnit(Address);
            NameInput.TextChanged += delegate { if (!updating) nameDirty = true; };
            UnitInput.TextChanged += delegate { if (!updating) unitDirty = true; };
            NameInput.Dock = UnitInput.Dock = DockStyle.Fill;
            AddLabel(table, "名称：", 0, 0); table.Controls.Add(NameInput, 1, 0);
            AddLabel(table, "单位：", 2, 0); table.Controls.Add(UnitInput, 3, 0);
            Button saveLabel = Ui.Button("保存名称", 82, false); saveLabel.Dock = DockStyle.Fill; table.Controls.Add(saveLabel, 4, 0); table.SetColumnSpan(saveLabel, 2);
            saveLabel.Click += delegate { try { SaveLabel(); error.Text = "名称和单位已保存"; } catch (ArgumentException ex) { error.Text = ex.Message; } };
            if (area.Area >= 2)
            {
                AddLabel(table, "数值：", 0, 1); ValueInput.Dock = DockStyle.Fill; table.Controls.Add(ValueInput, 1, 1); table.SetColumnSpan(ValueInput, 3);
                ValueInput.TextChanged += delegate { if (!updating) dirty = true; };
                Button write = Ui.Button("写入数值", 82, true); writeButton = write; write.Dock = DockStyle.Fill; table.Controls.Add(write, 4, 1); table.SetColumnSpan(write, 2);
                write.Click += delegate { try { WriteValue(ValueInput.Text); } catch (ArgumentException ex) { error.Text = ex.Message; } };
                ValueInput.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { write.PerformClick(); e.SuppressKeyPress = true; } };
                Button parse = Ui.Button("单项解析设置", 115, false), reset = Ui.Button("使用整区默认", 115, false);
                parse.Dock = reset.Dock = DockStyle.Fill; table.Controls.Add(parse, 0, 2); table.SetColumnSpan(parse, 2); table.Controls.Add(reset, 2, 2); table.SetColumnSpan(reset, 4);
                parse.Click += delegate
                {
                    using (RuleEditor dialog = new RuleEditor(area.Presentation.GetRule(Address), AreaPanel.Names[area.Area] + " 地址 " + Address))
                        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) area.SetParseRule(Address, dialog.Result);
                };
                reset.Click += delegate { area.Presentation.Rules.Remove(Address); area.NotifyPresentation(); };
            }
            else
            {
                // 位对象没有解析控件；单位不适用，仅支持名称和卡片上的开关。
                table.GetControlFromPosition(2, 0).Visible = false; UnitInput.Visible = false;
                Label hint = new Label { Text = area.Area == 0 ? "开关发送05写线圈，确认后读回刷新。" : "离散输入只读；卡片显示设备上次成功读取的状态。", Dock = DockStyle.Fill, ForeColor = Color.SlateGray };
                table.Controls.Add(hint, 0, 1); table.SetColumnSpan(hint, 6); table.SetRowSpan(hint, 2);
            }
            error.Dock = DockStyle.Fill; error.ForeColor = Color.FromArgb(184, 55, 37); table.Controls.Add(error, 0, 3); table.SetColumnSpan(error, 6);
            Editor.Controls.Add(table);
        }
        private static void AddLabel(TableLayoutPanel table, string text, int column, int row)
        { table.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0) }, column, row); }
        public void SaveLabel()
        {
            area.Presentation.SetLabel(Address, nameDirty ? NameInput.Text.Trim() : area.Presentation.GetName(Address), area.Area < 2 ? area.Presentation.GetUnit(Address) : unitDirty ? UnitInput.Text.Trim() : area.Presentation.GetUnit(Address));
            nameDirty = unitDirty = false; area.NotifyPresentation(); RefreshValue();
        }
        public void SetEditorOpen(bool open)
        { opened = open; Header.BackColor = open ? UiTheme.SelectedSurface : UiTheme.Surface; UpdateTitle(); }
        // 展开、快速编辑错误和主机数据质量均属于显示状态，切换主题后保留。
        internal void ApplyTheme()
        {
            Header.BackColor = opened ? UiTheme.SelectedSurface : UiTheme.Surface;
            QuickValueInput.BackColor = quickValueInvalid ? UiTheme.InputErrorBackground : UiTheme.InputBackground;
            ValueLabel.ForeColor = area.HasFreshData ? UiTheme.ValueText : UiTheme.MutedText;
        }
        public void ToggleExpanded() { if (EditorRequested != null) EditorRequested(this); else SetEditorOpen(!opened); }
        private void UpdateTitle()
        {
            string name = area.Presentation.GetName(Address);
            Header.Text = (opened ? "▾ " : "▸ ") + (String.IsNullOrEmpty(name) ? AreaPanel.Names[area.Area] : name) + " · " + Address;
        }
        public bool BeginValueEdit()
        {
            if (area.Area < 2 || !area.CanWrite) return false;
            try
            {
                double value = AreaPresentation.Read(bank, area.Area, Address, area.Presentation.GetRule(Address));
                QuickValueInput.Text = value.ToString("R", CultureInfo.InvariantCulture); quickValueInvalid = false; QuickValueInput.BackColor = UiTheme.InputBackground;
                ValueLabel.Visible = false; QuickValueInput.Visible = true; QuickValueInput.BringToFront(); QuickValueInput.Focus(); QuickValueInput.SelectAll();
                information.SetToolTip(QuickValueInput, "Enter确认；Esc或点击别处取消。按当前解析规则写入。"); return true;
            }
            catch (ArgumentException ex) { information.SetToolTip(ValueLabel, ex.Message); return false; }
        }
        public bool CommitValueEdit()
        {
            if (!QuickValueInput.Visible) return false;
            try { WriteValue(QuickValueInput.Text); CancelValueEdit(); return true; }
            catch (ArgumentException ex)
            {
                error.Text = ex.Message; quickValueInvalid = true; QuickValueInput.BackColor = UiTheme.InputErrorBackground;
                information.SetToolTip(QuickValueInput, ex.Message + "\r\nEnter确认；Esc取消。"); QuickValueInput.SelectAll(); return false;
            }
        }
        public void CancelValueEdit()
        { if (QuickValueInput.Visible) { QuickValueInput.Visible = false; ValueLabel.Visible = area.Area >= 2; } }
        private void UpdateInformation()
        {
            string text = detail.Text;
            if (area.Area >= 2)
            {
                ParseRule rule = area.Presentation.GetRule(Address);
                text += "\r\n显示值 = (原值 " + rule.Operation + " " + rule.Operand.ToString(CultureInfo.InvariantCulture) + ") + " + rule.Offset.ToString(CultureInfo.InvariantCulture);
                text += area.CanWrite ? "\r\n双击数值快速编辑，Enter确认。" : "\r\n当前只读或未连接。";
            }
            foreach (Control control in new Control[] { this, Header, ValueLabel, Switch, lamp })
                if (information.GetToolTip(control) != text) information.SetToolTip(control, text);
            ValueLabel.Cursor = area.Area >= 2 && area.CanWrite ? Cursors.IBeam : Cursors.Default;
        }
        public void WriteValue(string text)
        {
            if (area.Area < 2) throw new ArgumentException("位对象请使用开关按钮。");
            if (!area.CanWrite) throw new ArgumentException(area.IsWritable ? "请先连接从机。" : "输入寄存器只读。");
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("请输入有效解析数值。");
            ushort[] words = ValueCodec.Encode(value, area.Presentation.GetRule(Address));
            area.SendWrite(Address, words, words.Length > 1); dirty = false; error.Text = "写入已排队，确认后读回刷新"; RefreshValue();
        }
        public void RefreshValue()
        {
            updating = true;
            try
            {
                UpdateTitle(); Switch.Enabled = area.CanWrite; if (!area.CanWrite) CancelValueEdit();
                ValueLabel.ForeColor = area.HasFreshData ? UiTheme.ValueText : UiTheme.MutedText;
                if (writeButton != null) writeButton.Enabled = area.CanWrite;
                ValueInput.ReadOnly = !area.CanWrite;
                if (!nameDirty && !NameInput.Focused) NameInput.Text = area.Presentation.GetName(Address);
                if (!unitDirty && !UnitInput.Focused) UnitInput.Text = area.Presentation.GetUnit(Address);
                if (area.Area < 2)
                {
                    bool on = bank.Get(area.Area, Address) != 0; Switch.Checked = on; Switch.Text = on ? "ON  /  1" : "OFF  /  0"; lamp.Active = on;
                    detail.Text = AreaPanel.Names[area.Area] + " · 地址 " + Address + " · " + AreaPanel.Functions[area.Area] + "\r\n" + area.QualityText; return;
                }
                ParseRule rule = area.Presentation.GetRule(Address); int width = ValueCodec.WordCount(rule);
                double value = AreaPresentation.Read(bank, area.Area, Address, rule);
                string text = double.IsNaN(value) ? "NaN" : double.IsInfinity(value) ? (value > 0 ? "+Infinity" : "-Infinity") : value.ToString("F" + rule.Decimals, CultureInfo.InvariantCulture);
                string unit = area.Presentation.GetUnit(Address); ValueLabel.Text = text + (String.IsNullOrEmpty(unit) ? "" : " " + unit);
                detail.Text = AreaPanel.Names[area.Area] + " · " + rule.DataType + " / " + rule.ByteOrder + "\r\n地址 " + Address + (width > 1 ? "～" + (Address + width - 1) : "") + " · " + area.QualityText;
                if (!dirty && !ValueInput.Focused) ValueInput.Text = value.ToString("R", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException ex) { ValueLabel.Text = "无法解析"; detail.Text = ex.Message; }
            finally { updating = false; UpdateInformation(); }
        }
        internal CardDraft CaptureDraft()
        {
            if (!dirty && !nameDirty && !unitDirty) return null;
            return new CardDraft { Value = ValueInput.Text, Name = NameInput.Text, Unit = UnitInput.Text,
                ValueDirty = dirty, NameDirty = nameDirty, UnitDirty = unitDirty };
        }
        internal void RestoreDraft(CardDraft draft)
        {
            EnsureEditor(); updating = true;
            try
            {
                if (draft.ValueDirty) ValueInput.Text = draft.Value;
                if (draft.NameDirty) NameInput.Text = draft.Name;
                if (draft.UnitDirty) UnitInput.Text = draft.Unit;
                dirty = draft.ValueDirty; nameDirty = draft.NameDirty; unitDirty = draft.UnitDirty;
            }
            finally { updating = false; }
        }
        protected override void Dispose(bool disposing) { if (disposing) { information.Dispose(); Editor.Dispose(); } base.Dispose(disposing); }
    }
    // 连续滚动的逻辑列表；只建立视口附近控件，不按寄存器数量分配Windows句柄。
    public sealed class CardView : Panel
    {
        private readonly AreaPanel[] areas;
        private readonly Func<RegisterBank> getBank;
        private readonly CardViewport viewport = new CardViewport();
        private readonly VScrollBar scrollbar = new VScrollBar();
        private readonly Label editorTitle = new Label();
        private readonly List<AddressCard> cards = new List<AddressCard>();
        private readonly Dictionary<int, AddressCard> realized = new Dictionary<int, AddressCard>();
        private readonly Dictionary<int, CardDraft> drafts = new Dictionary<int, CardDraft>();
        private readonly LinkedList<AddressCard> parked = new LinkedList<AddressCard>();
        private readonly Dictionary<int, LinkedListNode<AddressCard>> parkedByKey = new Dictionary<int, LinkedListNode<AddressCard>>();
        private const int CacheLimit = 64;
        private readonly TableLayoutPanel layout = new SmoothTable();
        private readonly Panel editorHost = new SmoothPanel(), editorBody = new SmoothPanel();
        private readonly Timer dragTimer = new Timer(), scrollTimer = new Timer();
        private List<CardKey> visibleLayout = new List<CardKey>();
        private CardKey[] customOrder = new CardKey[0];
        private AddressCard selected, dragSource;
        private int columns = 1, cardWidth = 264, scrollLimit, wheelDelta;
        private bool arranging, disposed, scrollPending;
        private const int RowHeight = 116;
        private const string DragFormat = "ModbusSimulator.CardKey";
        public IReadOnlyList<AddressCard> Cards { get { return cards.AsReadOnly(); } }
        public int TotalCardCount { get { return visibleLayout.Count; } }
        public int CachedCardCount { get { return parked.Count; } }
        public int ScrollOffset { get { return scrollbar.Value; } }
        public int SelectedArea { get; private set; }
        private static int Identity(int area, int address) { return area * RegisterBank.Capacity + address; }
        private static bool Matches(AddressCard card, CardKey key) { return card != null && !card.IsDisposed && card.AreaId == key.Area && card.Address == key.Address; }
        public CardKey[] CustomOrder
        {
            get
            {
                List<CardKey> result = new List<CardKey>(); RegisterBank bank = getBank();
                foreach (CardKey key in customOrder) if (key.Area >= 0 && key.Area < 4 && key.Address >= bank.Start(key.Area) && key.Address < bank.Start(key.Area) + bank.Count(key.Area)) result.Add(new CardKey { Area = key.Area, Address = key.Address });
                return result.ToArray();
            }
            set { List<CardKey> result = new List<CardKey>(); if (value != null) foreach (CardKey key in value) if (key != null) result.Add(new CardKey { Area = key.Area, Address = key.Address }); customOrder = result.ToArray(); }
        }
        public CardView(AreaPanel[] panels, Func<RegisterBank> model)
        {
            areas = panels; getBank = model; Dock = DockStyle.Fill; BackColor = Color.FromArgb(239, 244, 250); DoubleBuffered = true;
            layout.Dock = DockStyle.Fill; layout.ColumnCount = 1; layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            Panel canvas = new SmoothPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            viewport.Dock = DockStyle.Fill; viewport.Margin = new Padding(0); viewport.TabStop = true;
            scrollbar.Dock = DockStyle.Right; scrollbar.Width = SystemInformation.VerticalScrollBarWidth;
            canvas.Controls.Add(viewport); canvas.Controls.Add(scrollbar);
            viewport.SizeChanged += delegate { ArrangeViewport(); };
            scrollTimer.Interval = 16;
            scrollTimer.Tick += delegate { FlushPendingScroll(); };
            scrollbar.ValueChanged += delegate { if (!arranging) { scrollPending = true; scrollTimer.Start(); } };
            viewport.MouseWheel += ScrollWheel;
            editorHost.Dock = DockStyle.Fill; editorHost.Padding = new Padding(6, 0, 6, 0); editorHost.Visible = false;
            editorTitle.Dock = DockStyle.Top; editorTitle.Height = 27; editorTitle.TextAlign = ContentAlignment.MiddleLeft; editorTitle.ForeColor = Color.FromArgb(38, 75, 120);
            Button close = Ui.Button("收起", 64, false); close.Dock = DockStyle.Right; close.Click += delegate { CloseEditor(); ArrangeViewport(); };
            Panel title = new Panel { Dock = DockStyle.Top, Height = 28 }; title.Controls.Add(editorTitle); title.Controls.Add(close);
            editorBody.Dock = DockStyle.Fill; editorHost.Controls.Add(editorBody); editorHost.Controls.Add(title);
            layout.Controls.Add(canvas, 0, 0); layout.Controls.Add(editorHost, 0, 1); Controls.Add(layout);
            ContextMenuStrip sortMenu = new ContextMenuStrip();
            sortMenu.Items.Add("恢复默认排序", null, delegate { customOrder = new CardKey[0]; RebuildCore(true); ScrollToIndex(0); });
            viewport.ContextMenuStrip = sortMenu; viewport.Disposed += delegate { sortMenu.Dispose(); };
            dragTimer.Interval = 70;
            dragTimer.Tick += delegate
            {
                if (dragSource == null) return; Point point = viewport.PointToClient(Cursor.Position);
                if (point.X < 0 || point.X > viewport.Width) return;
                if (point.Y < 38 && point.Y > -30) SetScroll(scrollbar.Value - 36, true);
                else if (point.Y > viewport.Height - 38 && point.Y < viewport.Height + 30) SetScroll(scrollbar.Value + 36, true);
            };
            AddDropTarget(viewport, null);
            foreach (AreaPanel area in areas) area.DisplayFormatChanged += PresentationChanged;
            Rebuild();
        }
        private ParseRule RuleAt(int area, int address) { return areas[area].Presentation.GetRule(address); }
        private void PresentationChanged()
        {
            List<CardKey> next = CardLayout.Build(getBank(), RuleAt, customOrder); bool same = next.Count == visibleLayout.Count;
            if (same) for (int i = 0; i < next.Count; i++) if (next[i].Area != visibleLayout[i].Area || next[i].Address != visibleLayout[i].Address) { same = false; break; }
            if (same) RefreshValues(); else RebuildCore(true);
        }
        private void CacheDraft(AddressCard card)
        {
            int key = Identity(card.AreaId, card.Address); CardDraft draft = card.CaptureDraft();
            if (draft == null) drafts.Remove(key); else drafts[key] = draft;
        }
        private bool IsRealized(AddressCard card) { return realized.ContainsValue(card); }
        private AddressCard TakeParked(CardKey key)
        {
            LinkedListNode<AddressCard> node;
            if (!parkedByKey.TryGetValue(Identity(key.Area, key.Address), out node)) return null;
            parked.Remove(node); parkedByKey.Remove(Identity(key.Area, key.Address)); return node.Value;
        }
        private void ClearParked()
        {
            foreach (AddressCard card in parked) card.Dispose(); parked.Clear(); parkedByKey.Clear();
        }
        private void Release(AddressCard card)
        {
            if (card == null || card.IsDisposed) return;
            card.Visible = false; viewport.Controls.Remove(card);
            if (card == selected || card == dragSource) return;
            CacheDraft(card); int key = Identity(card.AreaId, card.Address);
            LinkedListNode<AddressCard> existing;
            if (parkedByKey.TryGetValue(key, out existing))
            {
                parked.Remove(existing); parkedByKey.Remove(key); if (existing.Value != card) existing.Value.Dispose();
            }
            parkedByKey.Add(key, parked.AddLast(card));
            while (parked.Count > CacheLimit)
            {
                AddressCard oldest = parked.First.Value; CacheDraft(oldest);
                parkedByKey.Remove(Identity(oldest.AreaId, oldest.Address)); parked.RemoveFirst(); oldest.Dispose();
            }
        }
        private void CloseEditor()
        {
            AddressCard old = selected;
            if (old != null) old.SetEditorOpen(false);
            editorBody.Controls.Clear(); selected = null; editorHost.Visible = false; layout.RowStyles[1].Height = 0;
            if (old != null && !IsRealized(old)) Release(old);
        }
        private void OpenEditor(AddressCard card)
        {
            if (selected == card) { CloseEditor(); ArrangeViewport(); return; }
            CloseEditor(); card.EnsureEditor(); selected = card; selected.SetEditorOpen(true); SelectedArea = card.AreaId;
            editorTitle.Text = AreaPanel.Names[card.AreaId] + " · 地址 " + card.Address + (card.AreaId < 2 ? "　编辑名称 · 线圈开关写设备，离散输入只读" : "　编辑名称、单位和数值");
            editorBody.Controls.Add(card.Editor); editorHost.Visible = true; layout.RowStyles[1].Height = 160; ArrangeViewport();
        }
        public void SelectArea(int area) { if (getBank().Count(area) == 0) Rebuild(); else Locate(area, getBank().Start(area)); }
        public void Locate(int area, int address)
        {
            CardKey owner = CardLayout.OwnerOf(getBank(), RuleAt, area, address); SelectedArea = area;
            int index = visibleLayout.FindIndex(delegate(CardKey key) { return key.Area == owner.Area && key.Address == owner.Address; });
            if (index < 0) { Rebuild(); index = visibleLayout.FindIndex(delegate(CardKey key) { return key.Area == owner.Area && key.Address == owner.Address; }); }
            if (index < 0) return;
            ScrollToIndex(index); AddressCard card;
            if (realized.TryGetValue(index, out card)) { if (selected != card) OpenEditor(card); ScrollToIndex(index); }
        }
        public void ScrollToIndex(int index)
        {
            if (visibleLayout.Count == 0) { SetScroll(0); return; }
            if (index < 0 || index >= visibleLayout.Count) throw new ArgumentOutOfRangeException("index");
            ArrangeViewport(false); SetScroll((index / columns) * RowHeight);
        }
        public void FlushPendingScroll()
        {
            scrollTimer.Stop();
            if (!scrollPending || disposed) return;
            scrollPending = false; RenderViewport();
        }
        private void SetScroll(int value, bool defer = false)
        {
            int target = Math.Max(0, Math.Min(scrollLimit, value));
            if (scrollbar.Value != target) scrollbar.Value = target;
            if (!defer) { if (scrollPending) FlushPendingScroll(); else RenderViewport(); }
        }
        private void ScrollWheel(object sender, MouseEventArgs e)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            int amount = lines < 0 ? Math.Max(RowHeight, viewport.Height - RowHeight) : Math.Max(1, lines) * 26;
            wheelDelta += e.Delta; int steps = wheelDelta / 120; wheelDelta %= 120;
            if (steps != 0) SetScroll(scrollbar.Value - steps * amount, true);
            HandledMouseEventArgs handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
        }
        public void MoveCard(CardKey source, CardKey target)
        {
            List<CardKey> moved = CardLayout.MoveBefore(visibleLayout, source, target); customOrder = moved.ToArray();
            int index = moved.FindIndex(delegate(CardKey key) { return key.Area == source.Area && key.Address == source.Address; });
            RebuildCore(true); ScrollToIndex(index);
        }
        public void Rebuild() { RebuildCore(false); }
        private void RebuildCore(bool keepDrafts)
        {
            if (disposed || IsDisposed) return;
            CloseEditor();
            foreach (AddressCard card in realized.Values) Release(card);
            realized.Clear(); cards.Clear(); viewport.Controls.Clear();
            if (!keepDrafts) { ClearParked(); drafts.Clear(); }
            visibleLayout = CardLayout.Build(getBank(), RuleAt, customOrder);
            // 配置缩小或换bank后，移除失效草稿，不为删除的数据保留悬空编辑内容。
            RegisterBank bank = getBank(); List<int> expired = new List<int>();
            foreach (int id in drafts.Keys) { int area = id / RegisterBank.Capacity, address = id % RegisterBank.Capacity; if (address < bank.Start(area) || address >= bank.Start(area) + bank.Count(area)) expired.Add(id); }
            foreach (int id in expired) drafts.Remove(id);
            ArrangeViewport();
        }
        private void ArrangeViewport(bool paint = true)
        {
            if (disposed || arranging || viewport.IsDisposed) return; arranging = true;
            try
            {
                int oldColumns = columns, anchor = scrollbar.Value / RowHeight * oldColumns;
                int available = Math.Max(270, viewport.ClientSize.Width - 8); columns = Math.Max(1, available / 276);
                cardWidth = Math.Max(264, available / columns - 12);
                int rows = (visibleLayout.Count + columns - 1) / columns, totalHeight = rows * RowHeight + 8;
                int height = Math.Max(1, viewport.ClientSize.Height); scrollLimit = Math.Max(0, totalHeight - height);
                int value = oldColumns != columns ? anchor / columns * RowHeight : scrollbar.Value;
                scrollbar.Maximum = Math.Max(0, totalHeight - 1); scrollbar.LargeChange = height; scrollbar.SmallChange = 52;
                scrollbar.Enabled = scrollLimit > 0; scrollbar.Value = Math.Max(0, Math.Min(scrollLimit, value));
            }
            finally { arranging = false; }
            if (paint) RenderViewport();
        }
        private void RenderViewport()
        {
            if (disposed || arranging || viewport.IsDisposed) return; scrollTimer.Stop(); scrollPending = false; arranging = true; viewport.BeginFrame(); viewport.SuspendLayout();
            try
            {
                int offset = scrollbar.Value, firstRow = Math.Max(0, offset / RowHeight - 1);
                int lastRow = (offset + Math.Max(1, viewport.Height)) / RowHeight + 1;
                int first = Math.Min(visibleLayout.Count, firstRow * columns), last = Math.Min(visibleLayout.Count, (lastRow + 1) * columns);
                List<int> removed = new List<int>(); foreach (int index in realized.Keys) if (index < first || index >= last) removed.Add(index);
                foreach (int index in removed) { AddressCard old = realized[index]; realized.Remove(index); Release(old); }
                for (int index = first; index < last; index++)
                {
                    AddressCard card;
                    if (!realized.TryGetValue(index, out card))
                    {
                        CardKey key = visibleLayout[index];
                        if (Matches(selected, key)) card = selected;
                        else if (Matches(dragSource, key)) card = dragSource;
                        else if ((card = TakeParked(key)) != null) card.RefreshValue();
                        else
                        {
                            card = new AddressCard(getBank(), areas[key.Area], key.Address); CardDraft draft;
                            if (drafts.TryGetValue(Identity(key.Area, key.Address), out draft)) card.RestoreDraft(draft);
                            card.EditorRequested += OpenEditor; InstallDrag(card); card.MouseWheel += ScrollWheel;
                            foreach (Control child in card.Controls) child.MouseWheel += ScrollWheel;
                        }
                        realized.Add(index, card); viewport.Controls.Add(card);
                    }
                    card.SetBounds(6 + index % columns * (cardWidth + 12), 6 + index / columns * RowHeight - offset, cardWidth, 104);
                    card.Visible = true;
                }
                cards.Clear(); for (int index = first; index < last; index++) cards.Add(realized[index]);
            }
            finally { viewport.ResumeLayout(false); arranging = false; viewport.EndFrame(); }
        }
        private void InstallDrag(AddressCard card)
        {
            Point start = Point.Empty; bool pressed = false;
            card.DragHandle.MouseDown += delegate(object sender, MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; start = e.Location; };
            card.DragHandle.MouseUp += delegate { pressed = false; };
            card.DragHandle.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!pressed || e.Button != MouseButtons.Left || (Math.Abs(e.X - start.X) < SystemInformation.DragSize.Width / 2 && Math.Abs(e.Y - start.Y) < SystemInformation.DragSize.Height / 2)) return;
                pressed = false; DataObject data = new DataObject(); data.SetData(DragFormat, card.AreaId + ":" + card.Address);
                dragSource = card; dragTimer.Start();
                try { card.DragHandle.DoDragDrop(data, DragDropEffects.Move); }
                finally { dragTimer.Stop(); dragSource = null; if (!IsRealized(card) && card != selected) Release(card); }
            };
            AddDropTarget(card, card); foreach (Control child in card.Controls) AddDropTarget(child, card);
        }
        private void AddDropTarget(Control control, AddressCard target)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = GetDragKey(e.Data) != null ? DragDropEffects.Move : DragDropEffects.None; };
            control.DragOver += delegate(object sender, DragEventArgs e) { e.Effect = GetDragKey(e.Data) != null ? DragDropEffects.Move : DragDropEffects.None; };
            control.DragDrop += delegate(object sender, DragEventArgs e)
            {
                CardKey key = GetDragKey(e.Data); if (key == null) return;
                AddressCard destination = target;
                if (destination == null && cards.Count > 0) destination = cards[cards.Count - 1];
                if (destination == null || (key.Area == destination.AreaId && key.Address == destination.Address)) return;
                MoveCard(key, new CardKey { Area = destination.AreaId, Address = destination.Address });
            };
        }
        private CardKey GetDragKey(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DragFormat)) return null;
            string text = data.GetData(DragFormat) as string; if (text == null) return null; string[] parts = text.Split(':'); int area, address;
            if (parts.Length != 2 || !int.TryParse(parts[0], out area) || !int.TryParse(parts[1], out address)) return null;
            foreach (CardKey key in visibleLayout) if (key.Area == area && key.Address == address) return new CardKey { Area = area, Address = address };
            return null;
        }
        public void RefreshValues()
        {
            if (!Visible) return; foreach (AddressCard card in cards) card.RefreshValue();
            if (selected != null && !IsRealized(selected)) selected.RefreshValue();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true; dragTimer.Stop(); dragTimer.Dispose(); scrollTimer.Stop(); scrollTimer.Dispose();
                foreach (AreaPanel area in areas) area.DisplayFormatChanged -= PresentationChanged;
                AddressCard pinned = selected; selected = null; editorBody.Controls.Clear();
                if (pinned != null && !IsRealized(pinned)) pinned.Dispose();
                foreach (AddressCard card in realized.Values) card.Dispose(); realized.Clear(); cards.Clear(); ClearParked(); drafts.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
