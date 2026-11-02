using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
internal static class QuickCardChecks
{
    private static int passed;
    private static object Field(object obj,string name){return obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(obj);}
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS "+text);}
    private static object Locate(object view,int area,int address){view.GetType().GetMethod("Locate").Invoke(view,new object[]{area,address});foreach(object card in (System.Collections.IEnumerable)view.GetType().GetProperty("Cards").GetValue(view,null))if((int)card.GetType().GetProperty("AreaId").GetValue(card,null)==area&&(int)Field(card,"Address")==address)return card;throw new Exception("card missing");}
    private static void Key(Control control,Keys key){typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(control,new object[]{new KeyEventArgs(key)});}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Assembly assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));Type type=assembly.GetType("ModbusSimulator.MainForm")??assembly.GetType("ModbusMaster.MainForm");bool master=type.Namespace=="ModbusMaster";
            using(Form form=(Form)Activator.CreateInstance(type))
            {
                form.Show();((ComboBox)Field(form,"displayMode")).SelectedIndex=4;Application.DoEvents();object views=Field(form,"Views"),view=Field(views,"Cards"),bank=type.GetProperty("Bank").GetValue(form,null);Array areas=(Array)Field(form,"Areas");
                object card=Locate(view,2,0);Check(!((Control)Field(card,"detail")).Visible,"card details hidden from visible surface");
                MethodInfo begin=card.GetType().GetMethod("BeginValueEdit");Check(begin!=null,"quick edit API available");
                ushort[] queued=null;if(master)
                {
                    Check(!(bool)begin.Invoke(card,null),"offline master cannot quick-write cache");
                    areas.GetValue(2).GetType().GetMethod("SetWriteEnabled").Invoke(areas.GetValue(2),new object[]{true});
                    Func<int,ushort[],bool,bool> sender=delegate(int address,ushort[] values,bool multiple){queued=values;return true;};areas.GetValue(2).GetType().GetField("RequestWrite").SetValue(areas.GetValue(2),sender);
                }
                ((Control)Field(card,"ValueLabel")).GetType().GetMethod("OnDoubleClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(Field(card,"ValueLabel"),new object[]{EventArgs.Empty});
                TextBox quick=(TextBox)Field(card,"QuickValueInput");Check(quick.Visible,"value double-click opens inline edit");quick.Text="123";Key(quick,Keys.Enter);
                ushort actual=(ushort)bank.GetType().GetMethod("Get").Invoke(bank,new object[]{2,0});Check(master?queued!=null&&queued[0]==123&&actual==0:actual==123,"Enter uses proper local/remote write path");Check(!quick.Visible,"accepted quick edit closes");
                begin.Invoke(card,null);quick.Text="bad";Key(quick,Keys.Enter);Check(quick.Visible,"invalid number remains editable");Key(quick,Keys.Escape);Check(!quick.Visible,"Escape cancels inline edit");
                begin.Invoke(card,null);quick.Text="999";((ComboBox)Field(form,"displayMode")).SelectedIndex=0;Application.DoEvents();((ComboBox)Field(form,"displayMode")).SelectedIndex=4;Application.DoEvents();
                Check(!quick.Visible&&((Control)Field(card,"ValueLabel")).Visible,"switching views cancels quick edit and restores value label");
                object bit=Locate(view,0,0);Check(!(bool)bit.GetType().GetMethod("BeginValueEdit").Invoke(bit,null),"bit card keeps switch instead of numeric editor");
                if(master){object input=Locate(view,3,0);Check(!(bool)input.GetType().GetMethod("BeginValueEdit").Invoke(input,null),"master input register quick edit remains read only");}
                Check(((Control)card).Height==((Control)bit).Height,"all card types retain equal compact size");form.Close();
            }
            Console.WriteLine("TOTAL "+passed+", FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
