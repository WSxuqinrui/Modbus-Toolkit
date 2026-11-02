using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// 实际窗口检查：不触发项目链接、不打开 COM，并禁用真实布局状态读写。
internal static class SystemSettingsChecks
{
    private static int passed;
    private static object Field(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); passed++; }
    private static Control Named(Control root, string name)
    {
        if (root.Name == name) return root;
        foreach (Control child in root.Controls) { Control result = Named(child, name); if (result != null) return result; }
        return null;
    }
    private static bool Descendant(Control child, Control root)
    { for (Control current = child; current != null; current = current.Parent) if (current == root) return true; return false; }
    private static Rectangle Bounds(Control control)
    { return new Rectangle(control.Parent.PointToScreen(control.Location), control.Size); }
    private static void SameEdges(Control first, Control second, string message)
    { Rectangle a = Bounds(first), b = Bounds(second); Check(a.Left == b.Left && a.Right == b.Right, message + " " + a.Left + ".." + a.Right + " / " + b.Left + ".." + b.Right); }
    private static ScrollableControl Scroller(Control control)
    { for (Control current = control.Parent; current != null; current = current.Parent) { ScrollableControl scroll = current as ScrollableControl; if (scroll != null && scroll.AutoScroll) return scroll; } return null; }
    private static void Layout(Form form) { form.PerformLayout(); Application.DoEvents(); }
    private static void Geometry(Form form, Control system, ComboBox language, ComboBox theme, LinkLabel project, string name)
    {
        Control mode = (Control)Field(form, "mode"), selectedArea = (Control)Field(form, "selectedArea");
        SameEdges(language, mode, name + " language field matches connection fields");
        SameEdges(theme, mode, name + " theme field matches connection fields");
        SameEdges(project, mode, name + " project link matches connection fields");
        SameEdges(system, selectedArea, name + " system group matches data selector");
        Check(language.Width >= 90 && theme.Width >= 90 && project.Width >= 90, name + " fields remain usable");
        Check(language.Bottom <= theme.Top && theme.Bottom <= project.Top, name + " settings do not overlap");
        ScrollableControl scroll = Scroller(system);
        Check(scroll != null, name + " system settings belong to scrolling sidebar");
        scroll.ScrollControlIntoView(project); Layout(form);
        Rectangle view = scroll.RectangleToScreen(scroll.ClientRectangle), link = Bounds(project);
        Check(view.Contains(link), name + " project link is reachable by scrolling");
        Check(!scroll.HorizontalScroll.Visible, name + " sidebar needs no horizontal scroll");
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("Supply the application assembly.");
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type type = assembly.GetType("ModbusSimulator.MainForm") ?? assembly.GetType("ModbusMaster.MainForm");
            Type uiLanguage = assembly.GetType(type.Namespace + ".UiLanguage"), uiTheme = assembly.GetType(type.Namespace + ".UiTheme");
            using (Form form = (Form)Activator.CreateInstance(type))
            {
                form.Show(); Layout(form);
                ComboBox language = (ComboBox)Field(form, "languageMode"), theme = (ComboBox)Field(form, "themeMode");
                Control system = Named(form, "systemSettings"), header = Named(form, "systemSettingsHeader");
                Check(system != null && header != null, "system settings section is present");
                LinkLabel project = Named(system, "projectLink") as LinkLabel;
                Check(project != null, "project uses a keyboard-accessible link");
                SplitContainer split = (SplitContainer)Field(form, "BodySplit");
                Check(Descendant(system, split.Panel1) && Descendant(language, system) && Descendant(theme, system), "appearance controls are in the left system section");
                Control notice = (Control)Field(form, "notice");
                Check(notice.Parent.Controls.Count == 1, "footer contains only the status summary");
                TableLayoutPanel sidebar = header.Parent as TableLayoutPanel;
                Check(sidebar != null && sidebar.GetRow(header) > sidebar.GetRow((Control)Field(form, "tip")), "system section follows area definition and hints");
                Check(language.Items.Count == 5 && theme.Items.Count == 2, "language/theme index contracts are unchanged");
                Check((string)theme.Items[0] == "明亮模式" && (string)theme.Items[1] == "暗黑模式", "Chinese theme choices use mode names");
                Type info = assembly.GetType(type.Namespace + ".ProjectInfo");
                string url = (string)info.GetField("RepositoryUrl").GetRawConstantValue(), name = (string)info.GetField("RepositoryName").GetRawConstantValue();
                Check(project.Text == name && project.Links.Count == 1 && Convert.ToString(project.Links[0].LinkData) == url, "project link carries the configured repository address");
                Check(project.AccessibleDescription == url && new Uri(url).Scheme == "https", "full project address is available to accessibility tools");

                string[] headers = { "系统设置", "System settings", "システム設定", "시스템 설정", "Системные настройки" };
                string[] projects = { "项目地址", "Project", "プロジェクト", "프로젝트", "Проект" };
                string[] light = { "明亮模式", "Light mode", "ライトモード", "라이트 모드", "Светлый режим" };
                string[] dark = { "暗黑模式", "Dark mode", "ダークモード", "다크 모드", "Тёмный режим" };
                for (int index = 0; index < 5; index++)
                {
                    language.SelectedIndex = index; Layout(form);
                    Check(header.Text == headers[index], "system header translates immediately " + index);
                    Check(Named(system, "projectAddressLabel").Text == projects[index], "project caption translates immediately " + index);
                    Check(Convert.ToString(uiLanguage.GetMethod("ComboDisplayText").Invoke(null, new object[] { theme, theme.Items[0] })) == light[index], "light mode display translation " + index);
                    Check(Convert.ToString(uiLanguage.GetMethod("ComboDisplayText").Invoke(null, new object[] { theme, theme.Items[1] })) == dark[index], "dark mode display translation " + index);
                    Check(language.SelectedIndex == index && theme.SelectedIndex == 0 && (string)language.Items[0] == "中文", "language selection preserves stored enum values " + index);
                    theme.SelectedIndex = 1; Layout(form);
                    Check((bool)uiTheme.GetProperty("IsDark").GetValue(null, null), "dark mode applies immediately " + index);
                    Check(project.LinkColor.GetBrightness() > 0.5f, "project link is readable on dark surface " + index);
                    theme.SelectedIndex = 0; Layout(form);
                    Check(!(bool)uiTheme.GetProperty("IsDark").GetValue(null, null), "light mode applies immediately " + index);
                }
                language.SelectedIndex = 0; Layout(form);
                foreach (int width in new int[] { 280, 310, 380 })
                {
                    form.Size = new Size(width == 280 ? 980 : 1240, 720); split.SplitterDistance = width; Layout(form);
                    Geometry(form, system, language, theme, project, "width=" + width);
                }
                // 连续125%缩放模拟125%和156.25%，覆盖侧栏滚动与共同边界。
                foreach (float factor in new float[] { 1.25f, 1.25f })
                { form.Scale(new SizeF(factor, factor)); Layout(form); Geometry(form, system, language, theme, project, "scale=" + factor); }
                form.Close();
            }
            if (args.Length > 1)
            {
                using (Form form = (Form)Activator.CreateInstance(type))
                {
                    form.Show(); form.Size = new Size(980, 720); Layout(form);
                    LinkLabel project = (LinkLabel)Named(form, "projectLink"); Scroller(project).ScrollControlIntoView(project); Layout(form);
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(args[1], type.Namespace + "-SystemSettings.png")); }
                    form.Close();
                }
            }
            Console.WriteLine("SystemSettings checks passed: " + passed + "; no COM or project-link launch."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
