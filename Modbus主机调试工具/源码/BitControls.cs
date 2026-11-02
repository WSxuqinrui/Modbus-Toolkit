using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ModbusMaster
{
    // 保留CheckBox的键盘与辅助功能语义，绘制圆角轨道和圆形滑块。
    public sealed class PillSwitch : CheckBox
    {
        public PillSwitch()
        {
            AutoSize = false; Size = new Size(96, 42); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? UiTheme.Surface : Parent.BackColor);
            // 极小尺寸时没有可用轨道；先退出，避免向 GDI+ 传入零/负数圆弧。
            float trackWidth = Width - 4, trackHeight = Math.Min(Height - 6, trackWidth);
            if (trackWidth <= 0 || trackHeight <= 0) return;
            RectangleF track = new RectangleF(2, (Height - trackHeight) / 2f, trackWidth, trackHeight);
            float diameter = track.Height;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(track.Left, track.Top, diameter, diameter, 90, 180);
                path.AddArc(track.Right - diameter, track.Top, diameter, diameter, 270, 180); path.CloseFigure();
                using (SolidBrush brush = new SolidBrush(Checked ? UiTheme.SwitchOn : UiTheme.SwitchOff)) e.Graphics.FillPath(brush, path);
            }
            float inset = Math.Min(4f, diameter / 4f), knob = diameter - inset * 2;
            float x = Checked ? track.Right - knob - inset : track.Left + inset;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(38, 45, 65, 85))) e.Graphics.FillEllipse(shadow, x, track.Top + inset + Math.Min(1f, inset / 4f), knob, knob);
            using (SolidBrush white = new SolidBrush(Color.White)) e.Graphics.FillEllipse(white, x, track.Top + inset, knob, knob);
            // 普通尺寸保留原有文字布局；不足以容纳文字时只显示轨道和滑块。
            if (Width >= 48 && Height >= 20)
            {
                Rectangle text = Checked ? new Rectangle(7, 4, Width / 2, Height - 8) : new Rectangle(Width / 2 - 3, 4, Width / 2 - 3, Height - 8);
                using (Font label = new Font("Segoe UI", 9, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, Checked ? "ON" : "OFF", label, text,
                        Checked ? Color.White : UiTheme.SwitchOffText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (Focused && ShowFocusCues && Width > 1 && Height > 1) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1));
        }
    }
    public sealed class StateLamp : Control
    {
        private bool active;
        public bool Active { get { return active; } set { if (active != value) { active = value; Invalidate(); } } }
        public StateLamp()
        { Size = new Size(50, 50); SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(Parent == null ? UiTheme.Surface : Parent.BackColor);
            float diameter = Math.Min(Width, Height) - 4, left = (Width - diameter) / 2, top = (Height - diameter) / 2;
            if (diameter <= 0) return;
            // 以原 50 像素控件的 46 像素晕圈为基准，圆芯和高光随实际直径同比缩放。
            // 晕圈与圆芯共用中心，避免缩为 32 像素时固定内缩量挤小圆芯。
            float scale = diameter / 46f;
            RectangleF core = new RectangleF(left + 7 * scale, top + 7 * scale, 32 * scale, 32 * scale);
            using (SolidBrush halo = new SolidBrush(Active ? UiTheme.LampOnHalo : UiTheme.LampOffHalo)) e.Graphics.FillEllipse(halo, left, top, diameter, diameter);
            using (SolidBrush lamp = new SolidBrush(Active ? UiTheme.LampOn : UiTheme.LampOff)) e.Graphics.FillEllipse(lamp, core);
            GraphicsState saved = e.Graphics.Save();
            try
            {
                // 高光位于圆芯左上；圆芯裁剪同时约束极小尺寸的抗锯齿边缘。
                using (GraphicsPath clip = new GraphicsPath())
                {
                    clip.AddEllipse(core);
                    e.Graphics.SetClip(clip, CombineMode.Intersect);
                    using (SolidBrush shine = new SolidBrush(Color.FromArgb(130, Color.White)))
                        e.Graphics.FillEllipse(shine, core.Left + core.Width * 0.1875f, core.Top + core.Height * 0.125f, core.Width * 0.25f, core.Height * 0.1875f);
                }
            }
            finally { e.Graphics.Restore(saved); }
        }
    }
}
