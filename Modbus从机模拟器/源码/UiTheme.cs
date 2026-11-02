using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace ModbusSimulator
{
    /// <summary>只改变控件颜色；0为既有白天配色，1为黑夜配色。应从UI线程调用。</summary>
    public static class UiTheme
    {
        private sealed class StyleColors
        {
            internal Color Back, Fore, SelectionBack, SelectionFore;
            internal StyleColors(DataGridViewCellStyle style)
            { Back = style.BackColor; Fore = style.ForeColor; SelectionBack = style.SelectionBackColor; SelectionFore = style.SelectionForeColor; }
            internal void Restore(DataGridViewCellStyle style)
            { style.BackColor = Back; style.ForeColor = Fore; style.SelectionBackColor = SelectionBack; style.SelectionForeColor = SelectionFore; }
        }
        private sealed class Original
        {
            internal Color Back, Fore, ActualBack, ActualFore, Border, Hover, Pressed, GridBack, GridLines;
            internal bool Visual, GridHeaders;
            internal FlatStyle Flat;
            internal TabDrawMode TabDraw;
            internal DrawMode ComboDraw;
            internal ComboWindow ComboPainter;
            internal StyleColors Cells, Rows, Alternate, Headers, RowHeaders;
            internal ToolStripRenderer Renderer;
            internal Original(Control control)
            {
                ActualBack = control.BackColor; ActualFore = control.ForeColor;
                Back = LocalColor(control, "BackColor", ActualBack); Fore = LocalColor(control, "ForeColor", ActualFore);
                ButtonBase button = control as ButtonBase;
                if (button != null) { Visual = button.UseVisualStyleBackColor; Flat = button.FlatStyle; Border = button.FlatAppearance.BorderColor; Hover = button.FlatAppearance.MouseOverBackColor; Pressed = button.FlatAppearance.MouseDownBackColor; }
                ComboBox combo = control as ComboBox; if (combo != null) { Flat = combo.FlatStyle; ComboDraw = combo.DrawMode; }
                DataGridView grid = control as DataGridView;
                if (grid != null)
                {
                    GridBack = grid.BackgroundColor; GridLines = grid.GridColor; GridHeaders = grid.EnableHeadersVisualStyles;
                    Cells = new StyleColors(grid.DefaultCellStyle); Rows = new StyleColors(grid.RowsDefaultCellStyle);
                    Alternate = new StyleColors(grid.AlternatingRowsDefaultCellStyle); Headers = new StyleColors(grid.ColumnHeadersDefaultCellStyle); RowHeaders = new StyleColors(grid.RowHeadersDefaultCellStyle);
                }
                TabControl tabs = control as TabControl; if (tabs != null) TabDraw = tabs.DrawMode;
                ToolStrip strip = control as ToolStrip; if (strip != null) Renderer = strip.Renderer;
            }
        }
        private sealed class ItemColors
        {
            internal Color Back, Fore;
            internal ItemColors(ToolStripItem item) { Back = item.BackColor; Fore = item.ForeColor; }
        }
        // 弱引用不延长临时卡片、浮窗和对话框的生命周期；原始颜色也不占用业务Tag。
        private static readonly ConditionalWeakTable<Control, Original> originals = new ConditionalWeakTable<Control, Original>();
        private static readonly ConditionalWeakTable<ToolStripItem, ItemColors> itemColors = new ConditionalWeakTable<ToolStripItem, ItemColors>();
        private static readonly List<WeakReference> roots = new List<WeakReference>();
        private static readonly ToolStripRenderer nightRenderer = new ToolStripProfessionalRenderer(new NightMenuColors());
        private static int current;
        public static int Current { get { return current; } }
        public static bool IsDark { get { return current == 1; } }
        public static Color Canvas { get { return IsDark ? Color.FromArgb(21, 28, 39) : Color.FromArgb(239, 244, 250); } }
        public static Color Surface { get { return IsDark ? Color.FromArgb(29, 39, 53) : Color.White; } }
        public static Color InputBackground { get { return IsDark ? Color.FromArgb(35, 47, 64) : Color.White; } }
        public static Color InputErrorBackground { get { return IsDark ? Color.FromArgb(83, 39, 49) : Color.MistyRose; } }
        public static Color Text { get { return IsDark ? Color.FromArgb(231, 238, 248) : Color.FromArgb(37, 55, 78); } }
        public static Color MutedText { get { return IsDark ? Color.FromArgb(173, 190, 211) : Color.SlateGray; } }
        public static Color ValueText { get { return IsDark ? Color.FromArgb(135, 191, 255) : Color.FromArgb(26, 65, 108); } }
        public static Color SuccessText { get { return IsDark ? Color.FromArgb(100, 221, 165) : Color.FromArgb(5, 130, 85); } }
        public static Color ErrorText { get { return IsDark ? Color.FromArgb(255, 151, 147) : Color.FromArgb(190, 50, 40); } }
        public static Color SelectedSurface { get { return IsDark ? Color.FromArgb(46, 77, 116) : Color.FromArgb(225, 237, 255); } }
        public static Color SwitchOn { get { return IsDark ? Color.FromArgb(18, 155, 111) : Color.FromArgb(16, 166, 112); } }
        public static Color SwitchOff { get { return IsDark ? Color.FromArgb(76, 94, 119) : Color.FromArgb(196, 207, 221); } }
        public static Color SwitchOffText { get { return IsDark ? Color.FromArgb(231, 238, 248) : Color.FromArgb(68, 84, 104); } }
        public static Color LampOnHalo { get { return IsDark ? Color.FromArgb(30, 73, 61) : Color.FromArgb(219, 247, 232); } }
        public static Color LampOffHalo { get { return IsDark ? Color.FromArgb(46, 59, 77) : Color.FromArgb(237, 242, 247); } }
        public static Color LampOn { get { return IsDark ? Color.FromArgb(58, 219, 150) : Color.FromArgb(18, 178, 113); } }
        public static Color LampOff { get { return IsDark ? Color.FromArgb(113, 132, 157) : Color.FromArgb(153, 170, 190); } }
        public static Color LogRx { get { return IsDark ? Color.FromArgb(130, 187, 255) : Color.FromArgb(30, 91, 190); } }
        public static Color LogTx { get { return IsDark ? Color.FromArgb(98, 219, 164) : Color.FromArgb(0, 125, 81); } }
        public static Color LogDefault { get { return IsDark ? MutedText : Color.FromArgb(85, 98, 115); } }

        public static void SetTheme(int index)
        {
            if (index < 0 || index > 1) throw new ArgumentOutOfRangeException("index");
            if (current == index) return; current = index;
            // 主题切换时才遍历；150ms数值刷新和日志追加不扫描控件树。
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                Control root = roots[i].Target as Control;
                if (root == null || root.IsDisposed) { roots.RemoveAt(i); continue; }
                ApplyTree(root);
            }
        }
        public static void Apply(Control root)
        {
            if (root == null || root.IsDisposed) return;
            bool found = false;
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                Control previous = roots[i].Target as Control;
                if (previous == null || previous.IsDisposed) roots.RemoveAt(i);
                else if (ReferenceEquals(previous, root)) found = true;
            }
            if (!found) roots.Add(new WeakReference(root)); ApplyTree(root);
        }
        private static Color LocalColor(Control control, string property, Color actual)
        { PropertyDescriptor descriptor = TypeDescriptor.GetProperties(control)[property]; return descriptor != null && descriptor.ShouldSerializeValue(control) ? actual : Color.Empty; }
        private static Original Remember(Control control)
        {
            Original original;
            if (originals.TryGetValue(control, out original)) return original;
            original = new Original(control); originals.Add(control, original);
            control.ControlAdded += Added; control.EnabledChanged += EnabledChanged;
            if (control.Parent is NumericUpDown && control.GetType().Name == "UpDownButtons") control.Paint += DrawNumericButtons;
            if (control is Label || control is ButtonBase) control.Paint += DrawDisabledText;
            ComboBox combo = control as ComboBox;
            if (combo != null) { original.ComboPainter = new ComboWindow(combo); combo.DrawItem += DrawComboItem; }
            DataGridView grid = control as DataGridView;
            if (grid != null) grid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e) { ApplyTree(e.Control); };
            TabControl tabs = control as TabControl; if (tabs != null) tabs.DrawItem += DrawTab;
            return original;
        }
        private static void Added(object sender, ControlEventArgs e) { ApplyTree(e.Control); }
        private static void EnabledChanged(object sender, EventArgs e) { Control control = (Control)sender; Original original; if (originals.TryGetValue(control, out original)) PaintControl(control, original); }
        private static void ApplyTree(Control control)
        {
            if (control == null || control.IsDisposed) return;
            Original original = Remember(control); PaintControl(control, original);
            if (control.ContextMenuStrip != null) ApplyTree(control.ContextMenuStrip);
            foreach (Control child in control.Controls) ApplyTree(child);
            AddressCard card = control as AddressCard; if (card != null) card.ApplyTheme();
            control.Invalidate();
        }
        private static Color NightText(Color original)
        {
            if (original == Color.SlateGray || original == SystemColors.GrayText || (Math.Abs(original.R - original.G) < 40 && original.B > original.R && original.R >= 80)) return MutedText;
            if (original.R > 130 && original.R > original.G * 1.35 && original.R > original.B * 1.25) return ErrorText;
            if (original.G > 80 && original.G > original.R * 1.25 && original.G > original.B * 1.15) return SuccessText;
            if (original.B > original.R * 1.2 && original.B > original.G * 1.1) return ValueText;
            return Text;
        }
        private static void PaintControl(Control control, Original original)
        {
            bool input = control is TextBoxBase || control is ComboBox || control is NumericUpDown;
            if (!IsDark) { control.BackColor = original.Back; control.ForeColor = original.Fore; }
            else
            {
                control.BackColor = input ? (control.Enabled ? InputBackground : Color.FromArgb(15, 22, 32)) :
                    control is Form || original.ActualBack.ToArgb() == Color.FromArgb(239, 244, 250).ToArgb() ? Canvas : Surface;
                control.ForeColor = control.Enabled ? NightText(original.ActualFore) : MutedText;
                if (original.ActualBack.ToArgb() == Color.MistyRose.ToArgb()) control.BackColor = InputErrorBackground;
            }
            ButtonBase button = control as ButtonBase;
            if (button != null)
            {
                if (!IsDark)
                { button.FlatStyle = original.Flat; button.UseVisualStyleBackColor = original.Visual; button.FlatAppearance.BorderColor = original.Border; button.FlatAppearance.MouseOverBackColor = original.Hover; button.FlatAppearance.MouseDownBackColor = original.Pressed; }
                else
                {
                    button.UseVisualStyleBackColor = false;
                    if ((button is CheckBox && !(button is PillSwitch)) || button is RadioButton) button.FlatStyle = FlatStyle.Flat;
                    bool primary = original.ActualBack.B > original.ActualBack.R + 50 && original.ActualBack.B > original.ActualBack.G + 30;
                    if (primary) { button.BackColor = Color.FromArgb(40, 103, 195); button.ForeColor = Color.White; }
                    button.FlatAppearance.BorderColor = Color.FromArgb(82, 104, 133); button.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 76, 108); button.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 88, 127);
                }
            }
            ComboBox combo = control as ComboBox;
            if (combo != null)
            {
                combo.FlatStyle = IsDark ? FlatStyle.Flat : original.Flat;
                combo.DrawMode = IsDark || UiLanguage.IsDisplayCombo(combo) ? DrawMode.OwnerDrawFixed : original.ComboDraw;
            }
            DataGridView grid = control as DataGridView;
            if (grid != null)
            {
                if (!IsDark)
                {
                    grid.BackgroundColor = original.GridBack; grid.GridColor = original.GridLines; grid.EnableHeadersVisualStyles = original.GridHeaders;
                    original.Cells.Restore(grid.DefaultCellStyle); original.Rows.Restore(grid.RowsDefaultCellStyle); original.Alternate.Restore(grid.AlternatingRowsDefaultCellStyle);
                    original.Headers.Restore(grid.ColumnHeadersDefaultCellStyle); original.RowHeaders.Restore(grid.RowHeadersDefaultCellStyle);
                }
                else
                {
                    grid.EnableHeadersVisualStyles = false; grid.BackgroundColor = Surface; grid.GridColor = Color.FromArgb(62, 78, 100);
                    GridStyle(grid.DefaultCellStyle, Surface); GridStyle(grid.RowsDefaultCellStyle, Surface);
                    GridStyle(grid.AlternatingRowsDefaultCellStyle, Color.FromArgb(34, 46, 63));
                    GridStyle(grid.ColumnHeadersDefaultCellStyle, Color.FromArgb(43, 59, 81)); GridStyle(grid.RowHeadersDefaultCellStyle, Color.FromArgb(43, 59, 81));
                }
            }
            TabControl tabs = control as TabControl; if (tabs != null) tabs.DrawMode = IsDark ? TabDrawMode.OwnerDrawFixed : original.TabDraw;
            TrafficLog log = control as TrafficLog; if (log != null) log.ApplyTheme();
            ToolStrip strip = control as ToolStrip;
            if (strip != null) { strip.Renderer = IsDark ? nightRenderer : original.Renderer; foreach (ToolStripItem item in strip.Items) PaintItem(item); }
        }
        private static void GridStyle(DataGridViewCellStyle style, Color back)
        { style.BackColor = back; style.ForeColor = Text; style.SelectionBackColor = Color.FromArgb(49, 82, 124); style.SelectionForeColor = Color.FromArgb(242, 247, 255); }
        private static void PaintItem(ToolStripItem item)
        {
            ItemColors original = itemColors.GetValue(item, delegate(ToolStripItem key) { return new ItemColors(key); });
            item.BackColor = IsDark ? Surface : original.Back; item.ForeColor = IsDark ? Text : original.Fore;
            ToolStripDropDownItem dropdown = item as ToolStripDropDownItem;
            if (dropdown != null) { if (dropdown.HasDropDownItems) ApplyTree(dropdown.DropDown); foreach (ToolStripItem child in dropdown.DropDownItems) PaintItem(child); }
        }
        private static void DrawTab(object sender, DrawItemEventArgs e)
        {
            if (!IsDark) return; TabControl tabs = (TabControl)sender; if (e.Index < 0 || e.Index >= tabs.TabPages.Count) return;
            using (Brush brush = new SolidBrush(e.Index == tabs.SelectedIndex ? SelectedSurface : Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        // NumericUpDown保留原生键盘/鼠标行为，仅覆盖系统亮色箭头按钮的最后一层绘制。
        private static void DrawNumericButtons(object sender, PaintEventArgs e)
        {
            if (!IsDark) return; Control control = (Control)sender;
            int middle = control.Height / 2, center = control.Width / 2, half = Math.Max(2, Math.Min(control.Width / 4, 4));
            using (Brush back = new SolidBrush(control.Enabled ? InputBackground : Color.FromArgb(15, 22, 32))) e.Graphics.FillRectangle(back, control.ClientRectangle);
            using (Pen line = new Pen(Color.FromArgb(82, 104, 133)))
            { e.Graphics.DrawRectangle(line, 0, 0, Math.Max(0, control.Width - 1), Math.Max(0, control.Height - 1)); e.Graphics.DrawLine(line, 0, middle, control.Width, middle); }
            using (Brush arrow = new SolidBrush(control.Enabled ? Text : MutedText))
            {
                int top = middle / 2, bottom = middle + (control.Height - middle) / 2;
                e.Graphics.FillPolygon(arrow, new Point[] { new Point(center - half, top + 2), new Point(center + half, top + 2), new Point(center, top - 2) });
                e.Graphics.FillPolygon(arrow, new Point[] { new Point(center - half, bottom - 2), new Point(center + half, bottom - 2), new Point(center, bottom + 2) });
            }
        }
        // 系统禁用色会忽略ForeColor，在深色背景上近似黑字；最后一层明确绘制可读文字。
        private static void DrawDisabledText(object sender, PaintEventArgs e)
        {
            Control control = (Control)sender; if (!IsDark || control.Enabled || control is PillSwitch) return;
            Label label = control as Label; ButtonBase button = control as ButtonBase;
            ContentAlignment align = label != null ? label.TextAlign : button.TextAlign;
            Rectangle text = control.ClientRectangle;
            using (Brush back = new SolidBrush(control.BackColor)) e.Graphics.FillRectangle(back, control.ClientRectangle);
            if (button is CheckBox || button is RadioButton)
            {
                int side = Math.Min(13, Math.Max(7, control.Height - 4)), top = (control.Height - side) / 2;
                Rectangle glyph = new Rectangle(1, top, side, side);
                using (Pen border = new Pen(MutedText))
                {
                    if (button is RadioButton) e.Graphics.DrawEllipse(border, glyph); else e.Graphics.DrawRectangle(border, glyph);
                    bool check = button is CheckBox ? ((CheckBox)button).Checked : ((RadioButton)button).Checked;
                    if (check)
                    {
                        if (button is RadioButton) { glyph.Inflate(-3, -3); using (Brush dot = new SolidBrush(MutedText)) e.Graphics.FillEllipse(dot, glyph); }
                        else { border.Width = 2; e.Graphics.DrawLines(border, new Point[] { new Point(4, top + side / 2), new Point(6, top + side - 4), new Point(side - 1, top + 3) }); }
                    }
                }
                text.X = side + 5; text.Width = Math.Max(0, text.Width - text.X); align = ContentAlignment.MiddleLeft;
            }
            else if (button != null && button.FlatStyle == FlatStyle.Flat && button.FlatAppearance.BorderSize > 0)
                using (Pen border = new Pen(Color.FromArgb(82, 104, 133))) e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, control.Width - 1), Math.Max(0, control.Height - 1));
            TextFormatFlags flags = TextFormatFlags.EndEllipsis;
            if (align == ContentAlignment.TopCenter || align == ContentAlignment.MiddleCenter || align == ContentAlignment.BottomCenter) flags |= TextFormatFlags.HorizontalCenter;
            else if (align == ContentAlignment.TopRight || align == ContentAlignment.MiddleRight || align == ContentAlignment.BottomRight) flags |= TextFormatFlags.Right;
            if (align == ContentAlignment.MiddleLeft || align == ContentAlignment.MiddleCenter || align == ContentAlignment.MiddleRight) flags |= TextFormatFlags.VerticalCenter;
            else if (align == ContentAlignment.BottomLeft || align == ContentAlignment.BottomCenter || align == ContentAlignment.BottomRight) flags |= TextFormatFlags.Bottom;
            TextRenderer.DrawText(e.Graphics, control.Text, control.Font, text, MutedText, flags);
        }
        private static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            if (!IsDark) return; ComboBox combo = (ComboBox)sender;
            Color back = (e.State & DrawItemState.Selected) != 0 ? SelectedSurface : InputBackground;
            using (Brush brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
            string text = e.Index >= 0 && e.Index < combo.Items.Count ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
            TextRenderer.DrawText(e.Graphics, UiLanguage.ComboDisplayText(combo, text), combo.Font, e.Bounds, combo.Enabled ? Text : MutedText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
        // 原生ComboBox在Disabled时强制使用系统亮色，WM_PAINT后仅覆盖外观。
        // 键盘、下拉选择、编辑子窗口和原始SelectedIndex全部仍由原生控件处理。
        private sealed class ComboWindow : NativeWindow
        {
            private readonly ComboBox owner;
            internal ComboWindow(ComboBox control)
            {
                owner = control; owner.HandleCreated += Created; owner.HandleDestroyed += Destroyed;
                if (owner.IsHandleCreated) AssignHandle(owner.Handle);
            }
            private void Created(object sender, EventArgs e) { AssignHandle(owner.Handle); }
            private void Destroyed(object sender, EventArgs e) { ReleaseHandle(); }
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                if (!IsDark || owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated || Handle == IntPtr.Zero || (message.Msg != 0x000F && message.Msg != 0x0317 && message.Msg != 0x0318)) return;
                if (message.Msg != 0x000F && message.WParam == IntPtr.Zero) return;
                using (Graphics graphics = message.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(message.WParam)) PaintCombo(graphics);
            }
            private void PaintCombo(Graphics graphics)
            {
                Rectangle bounds = owner.ClientRectangle; if (bounds.Width < 2 || bounds.Height < 2) return;
                int width = Math.Min(bounds.Width, SystemInformation.VerticalScrollBarWidth + 2);
                Rectangle arrow = new Rectangle(bounds.Right - width, 1, width - 1, Math.Max(0, bounds.Height - 2));
                Color back = owner.Enabled ? InputBackground : Color.FromArgb(15, 22, 32);
                using (Brush brush = new SolidBrush(back)) graphics.FillRectangle(brush, owner.DropDownStyle == ComboBoxStyle.DropDownList ? bounds : arrow);
                using (Pen border = new Pen(Color.FromArgb(82, 104, 133))) graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
                using (Brush brush = new SolidBrush(owner.Enabled ? Text : MutedText))
                { int x = arrow.Left + arrow.Width / 2, y = arrow.Top + arrow.Height / 2; graphics.FillPolygon(brush, new Point[] { new Point(x - 3, y - 1), new Point(x + 3, y - 1), new Point(x, y + 2) }); }
                if (owner.DropDownStyle == ComboBoxStyle.DropDownList)
                {
                    Rectangle text = new Rectangle(3, 1, Math.Max(0, bounds.Width - width - 3), bounds.Height - 2);
                    TextRenderer.DrawText(graphics, UiLanguage.ComboDisplayText(owner, owner.Text), owner.Font, text, owner.Enabled ? Text : MutedText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
        }
        private sealed class NightMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(29, 39, 53); } }
            public override Color ImageMarginGradientBegin { get { return ToolStripDropDownBackground; } }
            public override Color ImageMarginGradientMiddle { get { return ToolStripDropDownBackground; } }
            public override Color ImageMarginGradientEnd { get { return ToolStripDropDownBackground; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(46, 77, 116); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(82, 104, 133); } }
            public override Color MenuBorder { get { return Color.FromArgb(82, 104, 133); } }
        }
    }
}
