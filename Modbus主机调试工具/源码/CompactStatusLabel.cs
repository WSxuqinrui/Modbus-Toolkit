using System;
using System.Windows.Forms;

namespace ModbusMaster
{
    /// <summary>底栏只显示摘要；长路径、时间及完整数据质量保留在悬停提示中。</summary>
    public sealed class CompactStatusLabel : Label
    {
        private readonly ToolTip information = new ToolTip { ShowAlways = true, AutoPopDelay = 30000 };
        private string summarySource, detailsSource;
        private bool settingSummary;
        public string Details { get; private set; }
        public CompactStatusLabel() { AutoEllipsis = true; }
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e); if (settingSummary) return;
            // 翻译器改写的是可见摘要，不能因此用摘要覆盖独立的完整说明。
            if (summarySource != null && Text == UiLanguage.Translate(summarySource)) SetDetails(UiLanguage.Translate(detailsSource));
            else { summarySource = Text; detailsSource = Text; SetDetails(Text); }
        }
        private void SetDetails(string value)
        {
            value = value ?? "";
            if (Details == value) return;
            Details = value; if (information != null) information.SetToolTip(this, value);
        }
        public void SetStatus(string summary, string details)
        {
            summarySource = summary; detailsSource = details;
            settingSummary = true; try { if (Text != summary) Text = summary; } finally { settingSummary = false; }
            SetDetails(UiLanguage.Translate(details));
        }
        protected override void Dispose(bool disposing)
        { if (disposing) information.Dispose(); base.Dispose(disposing); }
    }
}
