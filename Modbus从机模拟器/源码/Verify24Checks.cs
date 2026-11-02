using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;
internal static class Verify24Checks
{
    private static int passed;
    private static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
    private static void Check(bool yes,string label){if(!yes)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
    private static bool HasPageButton(Control root){if(root is Button&&(root.Text=="上一页"||root.Text=="下一页"))return true;foreach(Control c in root.Controls)if(HasPageButton(c))return true;return false;}
    private static int Total(CardView view){return (int)view.GetType().GetProperty("TotalCardCount").GetValue(view,null);}
    private static void Scroll(CardView view,int index){view.GetType().GetMethod("ScrollToIndex").Invoke(view,new object[]{index});Application.DoEvents();}
    private static AddressCard Find(CardView view,int area,int address){view.Locate(area,address);foreach(AddressCard card in view.Cards)if(card.AreaId==area&&card.Address==address)return card;throw new Exception("card missing");}
    private static void Capture(Form form,string path){Application.DoEvents();using(Bitmap b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(path,System.Drawing.Imaging.ImageFormat.Png);}}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(MainForm form=new MainForm())
            {
                form.Show();Application.DoEvents();Field<ComboBox>(form,"displayMode").SelectedIndex=4;Application.DoEvents();CardView view=form.Views.Cards;
                Check(!HasPageButton(view),"single scrolling view has no previous/next page controls");
                Check(Total(view)==40,"all four areas belong to one 40-card scroll range");
                Panel viewport=Field<Panel>(view,"viewport");int beforeWheel=(int)view.GetType().GetProperty("ScrollOffset").GetValue(view,null);
                for(int i=0;i<4;i++)typeof(Control).GetMethod("OnMouseWheel",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(viewport,new object[]{new HandledMouseEventArgs(MouseButtons.None,0,10,10,-30)});
                Check((int)view.GetType().GetProperty("ScrollOffset").GetValue(view,null)>beforeWheel,"small wheel deltas accumulate for smooth scrolling");
                AddressCard head=Find(view,2,0);head.ValueInput.Text="99";head.NameInput.Text="草稿名称";head.UnitInput.Text="MPa";if(head.IsExpanded)head.Header.PerformClick();
                Scroll(view,39);bool tail=false;foreach(AddressCard card in view.Cards)if(card.AreaId==1&&card.Address==9)tail=true;Check(tail,"scrollbar reaches last card across all areas without paging");
                head=Find(view,2,0);Check(head.ValueInput.Text=="99"&&head.NameInput.Text=="草稿名称"&&head.UnitInput.Text=="MPa","draft survives leaving and returning to visible viewport");
                view.MoveCard(new CardKey{Area=1,Address=9},new CardKey{Area=2,Address=0});Check(view.Cards[0].AreaId==1&&view.Cards[0].Address==9,"sorting can move a distant card into the first position");
                view.CustomOrder=new CardKey[0];view.Rebuild();
                for(int area=0;area<4;area++)form.Areas[area].ApplyDefinition(0,65536);Application.DoEvents();
                Check(Total(view)==262144,"full four-bank address space remains one logical list");
                Check(view.Cards.Count<100,"large list creates only viewport controls");Scroll(view,262143);
                tail=false;foreach(AddressCard card in view.Cards)if(card.AreaId==1&&card.Address==65535)tail=true;Check(tail,"one scroll range reaches final address 65535");
                head=Find(view,2,0);form.Areas[2].SetParseRule(0,new ParseRule{DataType="Float64"});Check(Total(view)==262141,"64bit hiding applies to whole scroll range");
                view.Locate(2,3);bool owner=false;foreach(AddressCard card in view.Cards)if(card.AreaId==2&&card.Address==0&&card.IsExpanded)owner=true;Check(owner,"hidden address locates its visible owner in continuous list");
                for(int area=0;area<4;area++)form.Areas[area].ApplyDefinition(0,0);Check(Total(view)==0&&view.Cards.Count==0,"empty continuous view has no controls");
                for(int area=0;area<4;area++)form.Areas[area].ApplyDefinition(0,10);
                form.Bank.Set(0,0,1);Find(view,0,0);Capture(form,Path.Combine(args[0],"2.4连续卡片及状态灯.png"));
                Find(view,2,0);Capture(form,Path.Combine(args[0],"2.4纵向设置和卡片.png"));form.Close();
            }
            Console.WriteLine("TOTAL "+passed+", FAILED 0");return 0;
        }
        catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}
    }
}
