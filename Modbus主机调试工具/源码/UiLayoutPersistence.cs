using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ModbusMaster
{
    /// <summary>本机界面偏好与设备配置分开保存，不包含连接启动状态、目标、日志或寄存器值。</summary>
    public sealed class UiLayoutState
    {
        public int Version = 1;
        public bool SidebarCollapsed, LogCollapsed, RawCollapsed;
        public int SidebarWidth = 310, LogHeight = 104;
        public int Language, Theme;
        public string RawText = "";
        public bool RawHex = true;
        public int RawChecksum, RawByteOrder, RawNewline, RawPeriod = 1000;
        public CommunicationPreferences Communication;
        private const int MaximumDraftCharacters = 131072;
        private const int MaximumPreferenceSize = 1048576;
        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaximumPreferenceSize, RecursionLimit = 16 }; }
        private static UiLayoutState Deserialize(string json)
        {
            try { return Serializer().Deserialize<UiLayoutState>(json); }
            catch (Exception error)
            {
                // .NET转换器还会对数字空串抛IndexOutOfRange；仅在JSON转换边界回退。
                for (Exception current = error; current != null; current = current.InnerException)
                    if (current is FormatException || current is OverflowException || current is InvalidCastException || current is MissingMethodException || current is IndexOutOfRangeException) return null;
                throw;
            }
        }
        public static string DefaultPath(string role)
        {
            // 构建回归使用显式注入路径，避免读取或改动用户正在使用的布局。
            if (Environment.GetEnvironmentVariable("MODBUS_DISABLE_UI_STATE") == "1") return null;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return String.IsNullOrEmpty(local) ? null : Path.Combine(local, "ModbusTools", role, "layout.json");
        }
        public static UiLayoutState Load(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            try
            {
                // 草稿可到输入框的131072字符上限；预留JSON转义及UTF8编码空间。
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumPreferenceSize) return null;
                UiLayoutState value = Deserialize(File.ReadAllText(path, Encoding.UTF8));
                if (value == null || value.Version != 1) return null;
                value.SidebarWidth = Math.Max(280, Math.Min(2000, value.SidebarWidth));
                value.LogHeight = Math.Max(45, Math.Min(4000, value.LogHeight));
                value.Language = Math.Max(0, Math.Min(4, value.Language)); value.Theme = value.Theme == 1 ? 1 : 0;
                if (value.RawText == null || value.RawText.Length > MaximumDraftCharacters) value.RawText = "";
                if (value.RawChecksum < 0 || value.RawChecksum >= RawPayload.ChecksumNames.Length) value.RawChecksum = 0;
                if (value.RawByteOrder < 0 || value.RawByteOrder > 1) value.RawByteOrder = 0;
                if (value.RawNewline < 0 || value.RawNewline > 3) value.RawNewline = 0;
                if (value.RawPeriod < 20 || value.RawPeriod > 600000) value.RawPeriod = 1000;
                if (value.Communication != null) value.Communication.Normalize();
                return value;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }
        public bool Save(string path)
        {
            if (String.IsNullOrEmpty(path)) return true;
            string temporary = null;
            try
            {
                path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, Serializer().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                temporary = null; return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { } } }
        }
    }

    public sealed partial class MainForm
    {
        private readonly string uiLayoutPath;
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); UiLayoutState saved = UiLayoutState.Load(uiLayoutPath);
            languageMode.SelectedIndex = saved == null ? 0 : saved.Language; themeMode.SelectedIndex = saved == null ? 0 : saved.Theme; ApplyUiAppearance();
            if (saved == null) return;
            RestoreCommunicationPreferences(saved.Communication);
            int maximum = Math.Max(BodySplit.Panel1MinSize, BodySplit.ClientSize.Width - BodySplit.SplitterWidth - BodySplit.Panel2MinSize);
            BodySplit.SplitterDistance = Math.Max(BodySplit.Panel1MinSize, Math.Min(maximum, saved.SidebarWidth));
            trafficWorkspace.RestoreLayout(saved.LogCollapsed, saved.RawCollapsed, saved.LogHeight);
            setSidebarCollapsed(saved.SidebarCollapsed);
            // 只恢复编辑设置。即使上次退出时正在循环，也不保存/恢复启动标志或目标。
            rawSender.StopCycle(null);
            rawSender.HexRadio.Checked = saved.RawHex; rawSender.AsciiRadio.Checked = !saved.RawHex;
            rawSender.ChecksumCombo.SelectedIndex = saved.RawChecksum; rawSender.ByteOrderCombo.SelectedIndex = saved.RawByteOrder;
            rawSender.NewlineCombo.SelectedIndex = saved.RawNewline; rawSender.PeriodInput.Value = saved.RawPeriod;
            rawSender.InputBox.Text = saved.RawText;
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e); if (e.Cancel) return;
            UiLayoutState saved = new UiLayoutState {
                SidebarCollapsed = BodySplit.Panel1Collapsed, SidebarWidth = BodySplit.SplitterDistance,
                LogCollapsed = trafficWorkspace.LogCollapsed, RawCollapsed = trafficWorkspace.RawCollapsed,
                LogHeight = trafficWorkspace.ExpandedLogHeight, Language = languageMode.SelectedIndex, Theme = themeMode.SelectedIndex,
                RawText = rawSender.InputBox.Text, RawHex = rawSender.HexRadio.Checked,
                RawChecksum = rawSender.ChecksumCombo.SelectedIndex, RawByteOrder = rawSender.ByteOrderCombo.SelectedIndex,
                RawNewline = rawSender.NewlineCombo.SelectedIndex, RawPeriod = (int)rawSender.PeriodInput.Value,
                Communication = CaptureCommunicationPreferences()
            };
            if (!saved.Save(uiLayoutPath)) AddLog("界面布局未能保存，下次将使用上次成功保存的布局。");
        }
        private void ApplyUiAppearance()
        {
            if (languageMode.SelectedIndex < 0 || themeMode.SelectedIndex < 0) return;
            UiLanguage.SetLanguage(languageMode.SelectedIndex); UiTheme.SetTheme(themeMode.SelectedIndex);
            UiLanguage.Apply(this); UiTheme.Apply(this);
            foreach (Form window in Application.OpenForms)
                if (window != this && window.GetType().Namespace == GetType().Namespace) { UiLanguage.Apply(window); UiTheme.Apply(window); }
            PerformLayout();
        }
    }
}
