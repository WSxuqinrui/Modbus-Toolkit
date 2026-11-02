using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ModbusSimulator;
internal static class ScrollPaintChecks
{
    private static T Field<T>(object source,string name){return (T)source.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(source);}
    private static bool HasToolbar(Control root){if(root is Button&&root.Text=="默认排序")return true;if(root is Label&&root.Text.Contains("向下滚动"))return true;foreach(Control c in root.Controls)if(HasToolbar(c))return true;return false;}
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest,int x,int y,int width,int height,IntPtr src,int sx,int sy,uint operation);
    private static Bitmap Capture(Control control)
    {
        Bitmap bitmap=new Bitmap(control.Width,control.Height);IntPtr source=GetDC(control.Handle);
        try{using(Graphics g=Graphics.FromImage(bitmap)){IntPtr target=g.GetHdc();try{if(!BitBlt(target,0,0,bitmap.Width,bitmap.Height,source,0,0,0x00CC0020))throw new Exception("client DC capture failed");}finally{g.ReleaseHdc(target);}}}
        finally{ReleaseDC(control.Handle,source);}return bitmap;
    }
    private static int Different(Bitmap a,Bitmap b){int count=0;for(int y=0;y<a.Height;y+=2)for(int x=0;x<a.Width;x+=2)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())count++;return count;}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(MainForm form=new MainForm())
            {
                form.Show();form.WindowState=FormWindowState.Maximized;form.SidebarToggle.PerformClick();Application.DoEvents();Field<ComboBox>(form,"displayMode").SelectedIndex=4;Application.DoEvents();
                CardView cards=form.Views.Cards;Panel viewport=Field<Panel>(cards,"viewport");VScrollBar scroll=Field<VScrollBar>(cards,"scrollbar");
                Console.WriteLine("VIEWPORT "+viewport.Size+" CONTROLS="+cards.Cards.Count+" TOOLBAR="+HasToolbar(cards));
                int limit=Math.Max(0,scroll.Maximum-scroll.LargeChange+1);Stopwatch clock=Stopwatch.StartNew();
                for(int i=0;i<160;i++){scroll.Value=Math.Abs(i%2==0?i*7:limit-i*3)%Math.Max(1,limit+1);Application.DoEvents();}
                MethodInfo flush=cards.GetType().GetMethod("FlushPendingScroll");if(flush!=null)flush.Invoke(cards,null);Application.DoEvents();
                clock.Stop();Console.WriteLine("SCROLL 160 REQUESTS (INCLUDING FINAL PAINT) "+clock.ElapsedMilliseconds+" ms");
                string tag=args.Length>1?args[1]:"scroll";
                using(Bitmap before=Capture(viewport))
                {
                    before.Save(Path.Combine(args[0],tag+"-live.png"));viewport.Refresh();Application.DoEvents();
                    using(Bitmap after=Capture(viewport)){after.Save(Path.Combine(args[0],tag+"-repaint.png"));int different=Different(before,after);Console.WriteLine("LIVE_VS_FORCED_REPAINT_DIFF="+different);if(different!=0)throw new Exception("viewport retains stale pixels after input burst");}
                }
                if(HasToolbar(cards)){Console.WriteLine("FAIL obsolete card toolbar still visible");return 1;}
                form.Close();
            }
            return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
