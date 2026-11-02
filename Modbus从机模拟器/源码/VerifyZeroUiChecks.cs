using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;
internal static class VerifyZeroUiChecks
{
    private static int checks;
    private static void Check(bool condition,string text){if(!condition)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    private static T Field<T>(object source,string name){return (T)source.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(source);}
    private static Button ButtonWith(Control root,string text){if(root is Button&&root.Text==text)return (Button)root;foreach(Control c in root.Controls){Button b=ButtonWith(c,text);if(b!=null)return b;}return null;}
    private static void DoubleCell(DataGridView grid,int column,int row)
    { typeof(DataGridView).GetMethod("OnCellDoubleClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(grid,new object[]{new DataGridViewCellEventArgs(column,row)}); }
    private static bool EditUsingDialog(Action open)
    {
        bool edited=false; int ticks=0;
        using(Timer closer=new Timer{Interval=30})
        {
            closer.Tick+=delegate
            {
                ticks++; RuleEditor editor=null; foreach(Form f in Application.OpenForms)if(f is RuleEditor){editor=(RuleEditor)f;break;}
                if(editor!=null){Field<ComboBox>(editor,"operation").SelectedItem="/";Field<TextBox>(editor,"operand").Text="10";((Button)editor.AcceptButton).PerformClick();edited=true;closer.Stop();}
                else if(ticks>30){closer.Stop();foreach(Form f in Application.OpenForms)if(f.Modal){f.DialogResult=DialogResult.Cancel;break;}}
            };
            closer.Start();open();closer.Stop();
        }
        return edited;
    }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(MainForm form=new MainForm())
            {
                form.Show();Application.DoEvents();ComboBox mode=Field<ComboBox>(form,"displayMode");Button restore=ButtonWith(form,"重新排列浮窗");
                Check(restore!=null&&!restore.Visible,"float rearrange hidden initially");
                form.Areas[0].ApplyDefinition(0,0);form.Areas[1].ApplyDefinition(0,0);form.Areas[3].ApplyDefinition(0,0);
                Check(form.Bank.Count(2)==10&&form.Views.ListGrid.RowCount==10,"zero quantity removes disabled rows from list");
                for(int i=0;i<4;i++)Check(form.Areas[i].CountInput.Minimum==0,"zero allowed in count input "+i);
                mode.SelectedIndex=4;foreach(AddressCard card in form.Views.Cards.Cards)Check(card.AreaId==2,"empty areas have no cards");
                mode.SelectedIndex=1;Application.DoEvents();Check(form.Areas[2].Visible&&form.Areas[0].Parent==null&&form.Areas[1].Parent==null&&form.Areas[3].Parent==null,"tiled mode hides empty panels");
                mode.SelectedIndex=2;Application.DoEvents();TabControl tabs=form.Areas[2].Parent.Parent as TabControl;Check(tabs!=null&&tabs.TabPages.Count==1,"tabs omit empty areas");
                mode.SelectedIndex=0;form.Bank.Set(2,0,100);
                Check(EditUsingDialog(delegate{DoubleCell(form.Views.ListGrid,5,0);}),"combined list double-click opens editable formula dialog");
                Check(form.Areas[2].Presentation.GetRule(0).Operation=="/"&&form.Areas[2].ParsedValue(0).StartsWith("10.000"),"list formula editor applies to shared model");
                mode.SelectedIndex=1;
                Check(EditUsingDialog(delegate{DoubleCell(form.Areas[2].Grid,4,1);}),"tiled grid double-click opens editable formula dialog");
                mode.SelectedIndex=2;Check(EditUsingDialog(delegate{DoubleCell(form.Areas[2].Grid,4,2);}),"tab grid double-click opens editable formula dialog");
                mode.SelectedIndex=3;Application.DoEvents();Form[] floats=Field<Form[]>(form.Views,"floats");int count=0;
                foreach(Form f in floats)if(f!=null){count++;Check(f.Visible&&f.Owner==null&&!f.TopMost,"float independent visible ordinary window");}
                Check(count==1&&floats[2]!=null,"only enabled area gets a float");
                Check(form.WindowState==FormWindowState.Minimized,"entering floating mode minimizes source only");
                form.WindowState=FormWindowState.Normal;form.Activate();Application.DoEvents();Check(restore.Visible,"float rearrange shown only in floating mode");
                Check(EditUsingDialog(delegate{DoubleCell(form.Areas[2].Grid,4,3);}),"floating grid double-click opens editable formula dialog");
                // 返回主窗时不存在owner约束；关闭浮窗仅隐藏，重排仍可恢复。
                floats[2].Close();Application.DoEvents();Check(!floats[2].Visible,"float close hides view without stopping model");form.Views.ShowFloats();Application.DoEvents();Check(floats[2].Visible,"rearrange restores hidden active float");
                form.WindowState=FormWindowState.Normal;mode.SelectedIndex=1;Application.DoEvents();Check(!restore.Visible,"rearrange hides again when leaving floating mode");
                form.Areas[2].ApplyDefinition(0,0);Application.DoEvents();Check(form.Areas[2].Parent==null,"disabling displayed area immediately removes pane");
                mode.SelectedIndex=3;Application.DoEvents();floats=Field<Form[]>(form.Views,"floats");count=0;foreach(Form f in floats)if(f!=null)count++;Check(count==0&&form.WindowState!=FormWindowState.Minimized,"all empty areas open no float and keep source visible");
                mode.SelectedIndex=4;Check(form.Views.Cards.Cards.Count==0,"all empty card view is stable");
                SimulatorConfiguration cfg=form.CaptureConfiguration();string path=Path.Combine(args[0],"2.3-空数据区.json");cfg.Save(path);form.ApplyConfiguration(SimulatorConfiguration.Load(path));Check(form.Views.ListGrid.RowCount==0,"all empty UI configuration roundtrip");
                form.Areas[2].ApplyDefinition(10,2);Check(form.Bank.Count(2)==2&&form.Bank.Get(2,10)==0,"disabled area can be enabled again");
                form.Close();
            }
            Console.WriteLine("TOTAL "+checks+", FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
