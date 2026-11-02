using System;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;
internal static class ScrollCoalesceChecks
{
    private static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(MainForm form=new MainForm())
            {
                form.Show();Field<ComboBox>(form,"displayMode").SelectedIndex=4;Application.DoEvents();CardView view=form.Views.Cards;
                VScrollBar scroll=Field<VScrollBar>(view,"scrollbar");AddressCard first=view.Cards[0];int moves=0;first.LocationChanged+=delegate{moves++;};
                for(int i=1;i<=40;i++)scroll.Value=i;
                if(moves!=0)throw new Exception("one input burst moved native card "+moves+" times before the next frame");
                DateTime stop=DateTime.UtcNow.AddMilliseconds(100);while(DateTime.UtcNow<stop)Application.DoEvents();
                if(moves!=1||first.Top!=6-40)throw new Exception("last scroll position was not presented in one update");
                Console.WriteLine("PASS 40 scroll requests coalesced to one native layout");form.Close();
            }
            return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
