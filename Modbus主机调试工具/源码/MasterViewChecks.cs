using System;
using System.Windows.Forms;
using ModbusMaster;
internal static class MasterViewChecks
{
    private static int checks;
    private static void Check(bool yes,string name){if(!yes)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    [STAThread] public static int Main()
    {
        try
        {
            RegisterBank bank=new RegisterBank();for(int i=0;i<4;i++)bank.Configure(i,0,10);
            using(AreaPanel holding=new AreaPanel(bank,2,delegate{}))
            {
                holding.SetText(0,"123");Check(bank.Get(2,0)==0,"offline master edit cannot change device cache");
                var type=holding.GetType();ushort[] queued=null;bool multiple=true;
                Func<int,ushort[],bool,bool> send=delegate(int address,ushort[] words,bool many){queued=words;multiple=many;return true;};
                type.GetField("RequestWrite").SetValue(holding,send);type.GetMethod("SetWriteEnabled").Invoke(holding,new object[]{true});
                holding.SetText(0,"123");Check(queued!=null&&queued[0]==123&&!multiple&&bank.Get(2,0)==0,"grid edit queues FC06 without optimistic cache mutation");
            }
            using(AreaPanel input=new AreaPanel(bank,3,delegate{}))
            {
                input.GetType().GetMethod("SetWriteEnabled").Invoke(input,new object[]{true});input.SetText(0,"345");Check(bank.Get(3,0)==0&&input.Grid.Columns[2].ReadOnly,"input registers remain read only");
            }
            Console.WriteLine("TOTAL "+checks+", FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
