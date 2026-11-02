using System;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;
internal static class ScrollReuseChecks
{
    private static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
    private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(MainForm form=new MainForm())
            {
                form.Show();Field<ComboBox>(form,"displayMode").SelectedIndex=4;Application.DoEvents();CardView view=form.Views.Cards;AddressCard first=view.Cards[0];
                Check(first.Editor.Controls.Count==0,"unopened card does not build editor controls");
                view.ScrollToIndex(39);view.ScrollToIndex(0);Check(Object.ReferenceEquals(first,view.Cards[0])&&!first.IsDisposed,"returning scroll reuses cached card instance");
                first.Header.PerformClick();Check(first.Editor.Controls.Count>0&&first.IsExpanded,"opening header creates editable settings on demand");
                first.NameInput.Text="缓存草稿";first.UnitInput.Text="kPa";first.ValueInput.Text="88";first.Header.PerformClick();
                for(int area=0;area<4;area++)form.Areas[area].ApplyDefinition(0,1000);
                view.Locate(2,0);first=null;foreach(AddressCard card in view.Cards)if(card.AreaId==2&&card.Address==0)first=card;
                first.ValueInput.Text="77";first.NameInput.Text="仍然保留";first.Header.PerformClick();
                for(int i=0;i<20;i++)view.ScrollToIndex(100+i*170);view.Locate(2,0);
                AddressCard restored=null;foreach(AddressCard card in view.Cards)if(card.AreaId==2&&card.Address==0)restored=card;
                Check(restored!=null&&restored.ValueInput.Text=="77"&&restored.NameInput.Text=="仍然保留","LRU eviction retains input drafts");
                int parked=(int)typeof(CardView).GetProperty("CachedCardCount").GetValue(view,null);Check(parked<=64&&view.Cards.Count<100,"card cache remains bounded");
                SimulatorConfiguration cfg=form.CaptureConfiguration();cfg.Areas[2].Values[0]=321;form.ApplyConfiguration(cfg);view.Locate(2,0);
                foreach(AddressCard card in view.Cards)if(card.AreaId==2&&card.Address==0)Check(card.ValueLabel.Text.StartsWith("321."),"loading replacement bank discards cached old model");
                form.Close();
            }
            Console.WriteLine("TOTAL 6, FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
