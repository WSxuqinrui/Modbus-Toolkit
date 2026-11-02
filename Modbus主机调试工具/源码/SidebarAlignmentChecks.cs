using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
internal static class SidebarAlignmentChecks
{
    private static int checks;
    private static object Field(object obj,string name){return obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(obj);}
    private static Rectangle Rect(Control control){return new Rectangle(control.Parent.PointToScreen(control.Location),control.Size);}
    private static Button ButtonWith(Control root,string text){if(root is Button&&root.Text==text)return (Button)root;foreach(Control child in root.Controls){Button found=ButtonWith(child,text);if(found!=null)return found;}return null;}
    private static void Check(bool yes,string text){if(!yes)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    private static void SameEdges(Control control,Rectangle expected,string label){Rectangle actual=Rect(control);Check(actual.Left==expected.Left&&actual.Right==expected.Right,label+" expected="+expected.Left+".."+expected.Right+" actual="+actual.Left+".."+actual.Right);}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Assembly assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));Type type=assembly.GetType("ModbusSimulator.MainForm")??assembly.GetType("ModbusMaster.MainForm");
            using(Form form=(Form)Activator.CreateInstance(type))
            {
                form.Show();Application.DoEvents();Control mode=(Control)Field(form,"mode"), selector=(Control)Field(form,"selectedArea");Array areas=(Array)Field(form,"Areas");SplitContainer split=(SplitContainer)Field(form,"BodySplit");
                foreach(int sidebarWidth in new int[]{280,310,380})
                {
                    form.Size=new Size(sidebarWidth==280?980:1240,860);split.SplitterDistance=sidebarWidth;Application.DoEvents();
                    Rectangle full=Rect(selector), input=Rect(mode);
                    int expectedRow=((TableLayoutPanel)mode.Parent).GetRowHeights()[0];
                    Check(Rect((Control)Field(form,"ip")).Top-input.Top==expectedRow,"IP wrapper keeps the same vertical row spacing");
                    foreach(string field in new string[]{"ip","port","unit","baud","parity","stopBits","dataBits"})SameEdges((Control)Field(form,field),input,"connection "+field+" width="+sidebarWidth);
                    SameEdges(ButtonWith(form,"刷新串口列表"),full,"refresh button width="+sidebarWidth);SameEdges((Control)Field(form,"toggle"),full,"connect/run button width="+sidebarWidth);
                    for(int area=0;area<4;area++)
                    {
                        ((ComboBox)selector).SelectedIndex=area==2?0:area==3?1:area==0?2:3;Application.DoEvents();object panel=areas.GetValue(area);
                        foreach(string field in new string[]{"StartInput","CountInput","Format","fill","jump"})SameEdges((Control)Field(panel,field),input,"area "+area+" "+field+" width="+sidebarWidth);
                        Control settings=(Control)Field(panel,"Settings");SameEdges(ButtonWith(settings,"应用数据区定义"),full,"apply area "+area);SameEdges(ButtonWith(settings,"定位"),full,"locate area "+area);
                        Button same=ButtonWith(settings,"常数填充")??ButtonWith(settings,"常数写入"), next=ButtonWith(settings,"递增填充")??ButtonWith(settings,"递增写入");
                        Check(Rect(same).Left==full.Left&&Rect(next).Right==full.Right&&same.Top==next.Top,"paired actions cover aligned row area "+area);
                        if(area>=2)SameEdges(ButtonWith(settings,"整区默认解析设置…"),full,"parse area "+area);
                    }
                    if(type.Namespace=="ModbusMaster")foreach(string name in new string[]{"timeoutMs","pollInterval"})SameEdges((Control)Field(form,name),input,"master "+name);
                }
                // 模拟WinForms按DPI缩放后的布局，核对共同边界随比例一致变化。
                foreach(float factor in new float[]{1.25f,1.2f})
                {
                    form.Scale(new SizeF(factor,factor));form.PerformLayout();Application.DoEvents();Rectangle input=Rect(mode);
                    for(int area=0;area<4;area++)
                    {
                        ((ComboBox)selector).SelectedIndex=area==2?0:area==3?1:area==0?2:3;Application.DoEvents();object panel=areas.GetValue(area);
                        SameEdges((Control)Field(panel,"StartInput"),input,"scaled sidebar range alignment factor="+factor+" area="+area);
                    }
                }
                form.Close();
            }
            // 独立正常尺寸截图，避免缩放模拟对交付预览尺寸造成影响。
            using(Form form=(Form)Activator.CreateInstance(type))
            {
                form.Show();((ComboBox)Field(form,"selectedArea")).SelectedIndex=3;Application.DoEvents();
                using(Bitmap bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(args[1],type.Namespace+"-侧栏对齐.png"));}form.Close();
            }
            Console.WriteLine("TOTAL "+checks+", FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
