using System;
using System.Drawing;
using System.Windows.Forms;
namespace ModbusSimulator
{
    // 只应用于左侧设置区；统一外层边界，避免嵌套TableLayoutPanel默认Margin叠加。
    internal static class SidebarLayout
    {
        private const float LabelWidth = 90;
        public static void Normalize(TableLayoutPanel table, bool fields)
        {
            table.Margin = new Padding(0); table.Padding = new Padding(0);
            if (fields && table.ColumnStyles.Count >= 2)
            { table.ColumnStyles[0].SizeType = SizeType.Absolute; table.ColumnStyles[0].Width = LabelWidth; }
            foreach (Control child in table.Controls)
            {
                if (child is Label || (child is Panel && (!fields || child is TableLayoutPanel))) child.Margin = new Padding(0);
                else child.Margin = new Padding(0, 3, 0, 3);
            }
        }
        // 设置区未选中时不在Form控件树内，DPI变化不会自动缩放它；挂回侧栏时同步比例。
        public static void MatchFieldScale(Control settings, float targetWidth)
        {
            if (settings.Controls.Count == 0) return;
            TableLayoutPanel options = settings.Controls[0] as TableLayoutPanel;
            if (options == null || options.ColumnStyles.Count == 0) return;
            float currentWidth = options.ColumnStyles[0].Width;
            if (currentWidth > 0 && Math.Abs(currentWidth - targetWidth) > 0.1f)
            {
                float ratio = targetWidth / currentWidth;
                settings.Scale(new SizeF(ratio, ratio));
            }
            MatchColumns(settings, targetWidth); settings.PerformLayout();
        }
        private static void MatchColumns(Control parent, float width)
        {
            TableLayoutPanel table = parent as TableLayoutPanel;
            if (table != null && table.ColumnCount == 2 && table.ColumnStyles.Count >= 2 && table.ColumnStyles[0].SizeType == SizeType.Absolute)
                table.ColumnStyles[0].Width = width;
            foreach (Control child in parent.Controls) if (child is Panel) MatchColumns(child, width);
        }
        public static TableLayoutPanel Actions(params Button[] buttons)
        {
            TableLayoutPanel row = new SmoothTable { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0), ColumnCount = buttons.Length, RowCount = 1 };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < buttons.Length; i++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length));
                buttons[i].Dock = DockStyle.Fill;
                buttons[i].Margin = new Padding(i == 0 ? 0 : 3, 3, i == buttons.Length - 1 ? 0 : 3, 3);
                row.Controls.Add(buttons[i], i, 0);
            }
            return row;
        }
    }
}
