using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ModbusMaster;
internal static class MasterUiChecks
{
    private static int checks;
    private static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);}
    private static void Check(bool yes,string name){if(!yes)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    private static void Wait(Func<bool> test,string name){Stopwatch watch=Stopwatch.StartNew();while(!test()&&watch.ElapsedMilliseconds<4000){Application.DoEvents();Thread.Sleep(5);}Check(test(),name);}
    private static int FreePort(){TcpListener listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private static AddressCard Card(CardView view,int area,int address){view.Locate(area,address);foreach(AddressCard card in view.Cards)if(card.AreaId==area&&card.Address==address)return card;throw new Exception("card not found");}
    private static void Capture(Form form,string file){Application.DoEvents();using(Bitmap b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(file,System.Drawing.Imaging.ImageFormat.Png);}}
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            ModbusSimulator.RegisterBank remote=new ModbusSimulator.RegisterBank();for(int i=0;i<4;i++)remote.Configure(i,0,1000);
            remote.Set(2,0,100);remote.Set(3,0,456);remote.Set(1,0,1);int port=FreePort();
            using(ModbusSimulator.SimulatorServer server=new ModbusSimulator.SimulatorServer(remote,1,null))
            using(MainForm form=new MainForm())
            {
                server.StartTcp(IPAddress.Loopback,port);form.Show();Application.DoEvents();
                Check(form.Session==null&&form.Bank.Count(2)==10&&!form.Areas[2].CanWrite,"starts disconnected with ten cached values per area");
                for(int i=0;i<4;i++)Check(!form.Areas[i].HasFreshData,"initial area "+i+" is not live data");
                ComboBox transport=Field<ComboBox>(form,"mode");int[] ids={1,3,2,0,4,5,6};
                for(int i=0;i<ids.Length;i++){transport.SelectedIndex=i;Check(form.CaptureConfiguration().Connection.Mode==ids[i],"transport UI stable id "+ids[i]);}
                form.SelectTransport(0);Field<ComboBox>(form,"com").Text="invalid hidden COM";Check(form.CaptureConfiguration().Connection.Mode==0,"hidden invalid COM does not block network configuration");MasterConfiguration cfg=form.CaptureConfiguration();cfg.Connection.Port=port;cfg.Display.Port=port;cfg.Connection.TimeoutMs=500;form.ApplyConfiguration(cfg);
                form.Connect();Wait(delegate{return form.Session!=null&&form.Session.IsConnected;},"UI starts background master connection");
                Wait(delegate{return form.Bank.Get(2,0)==100&&form.Bank.Get(3,0)==456&&form.Bank.Get(1,0)==1;},"connect triggers all four actual reads");
                Wait(delegate{return form.Areas[2].CanWrite&&form.Areas[2].HasFreshData;},"write controls and fresh status follow successful connection/read");
                form.Areas[2].SetText(0,"321");Wait(delegate{return remote.Get(2,0)==321&&form.Bank.Get(2,0)==321;},"table edit writes FC06 and readback updates cache");
                form.Areas[3].SetText(0,"999");Check(remote.Get(3,0)==456&&form.Bank.Get(3,0)==456,"input register table cannot write");
                ComboBox display=Field<ComboBox>(form,"displayMode");display.SelectedIndex=4;Application.DoEvents();
                AddressCard coil=Card(form.Views.Cards,0,0);coil.Switch.Checked=true;
                Wait(delegate{return remote.Get(0,0)==1&&form.Bank.Get(0,0)==1&&coil.Switch.Checked;},"coil card writes FC05 then displays device readback");
                AddressCard discrete=Card(form.Views.Cards,1,0);Check(!discrete.Switch.Enabled,"discrete input card switch remains read only");
                AddressCard input=Card(form.Views.Cards,3,0);bool denied=false;try{input.WriteValue("999");}catch(ArgumentException){denied=true;}Check(denied&&input.ValueInput.ReadOnly,"input register card is read only");
                form.Areas[2].SetParseRule(0,new ParseRule{DataType="Float32",Unit="kPa"});form.Areas[2].Presentation.SetLabel(0,"压力值","kPa");
                AddressCard pressure=Card(form.Views.Cards,2,0);pressure.WriteValue("25");
                Wait(delegate{return remote.Get(2,0)==0x41C8&&remote.Get(2,1)==0&&form.Bank.Get(2,0)==0x41C8;},"parsed Float32 card writes two words with FC10 and reads back");
                pressure.RefreshValue();Check(pressure.Header.Text.Contains("压力值")&&pressure.ValueLabel.Text.Contains("25.000 kPa"),"name unit and parsed display share actual device words");
                Check(form.Views.Cards.TotalCardCount==39,"32bit tail card hidden in continuous view");
                Check(form.QueueWrite(0,0,new ushort[]{0,1,1},true),"multiple coil request accepted");Wait(delegate{return remote.Get(0,0)==0&&remote.Get(0,1)==1&&form.Bank.Get(0,1)==1;},"FC0F multiple coils write and readback");
                Check(form.QueueWrite(2,4,new ushort[]{0x1111,0x2222},true),"multiple register request accepted");Wait(delegate{return remote.Get(2,5)==0x2222&&form.Bank.Get(2,4)==0x1111;},"FC10 multiple registers write and readback");
                Check(!form.QueueWrite(3,0,new ushort[]{999},false),"manual queue cannot write input objects");
                Field<NumericUpDown>(form,"pollInterval").Value=50;form.SetPolling(true);remote.Set(2,2,777);
                Wait(delegate{return form.Bank.Get(2,2)==777;},"automatic polling refreshes remote changes");form.SetPolling(false);Wait(delegate{return !form.Session.IsBusy;},"polling can stop without blocking UI");
                string saved=Path.Combine(args[0],"主机UI配置.json");form.CaptureConfiguration().Save(saved);form.Disconnect();
                Check(form.Session==null&&!form.Areas[2].CanWrite&&!form.Areas[2].HasFreshData,"disconnect disables writes and marks old values");
                form.ApplyConfiguration(MasterConfiguration.Load(saved));Check(form.Session==null&&form.Areas[2].Presentation.GetName(0)=="压力值","load restores metadata without auto connecting");
                for(int i=0;i<4;i++)Check(!form.Areas[i].HasFreshData,"loaded snapshot area "+i+" not claimed fresh");
                foreach(int kind in new int[]{1,2,0,4}){display.SelectedIndex=kind;Application.DoEvents();Check(form.Views.CurrentMode==kind,"display mode works "+kind);}
                form.Areas[0].ApplyDefinition(0,0);form.Areas[1].ApplyDefinition(0,0);display.SelectedIndex=3;Application.DoEvents();
                Form[] floats=Field<Form[]>(form.Views,"floats");Check(floats[0]==null&&floats[1]==null&&floats[2]!=null&&floats[2].Owner==null&&!floats[2].TopMost,"floats omit empty areas and do not stay on top");
                form.WindowState=FormWindowState.Normal;display.SelectedIndex=4;Card(form.Views.Cards,2,0);Capture(form,Path.Combine(args[0],"主机卡片界面.png"));
                form.SidebarToggle.PerformClick();Capture(form,Path.Combine(args[0],"主机收缩设置.png"));form.SidebarToggle.PerformClick();
                display.SelectedIndex=0;Capture(form,Path.Combine(args[0],"主机列表界面.png"));form.Size=form.MinimumSize;Capture(form,Path.Combine(args[0],"主机最小窗口.png"));
                Check(WriteDialog.ParseValues("100,0x00C8",false,false)[1]==200,"write dialog parses multiple decimal and HEX values");
                Check(WriteDialog.ParseValues("-1",false,true)[0]==65535,"write dialog supports signed raw bits");
                denied=false;try{WriteDialog.ParseValues("2",true,false);}catch(ArgumentException){denied=true;}Check(denied,"write dialog validates bit values");
                form.Close();
            }
            Console.WriteLine("TOTAL "+checks+", FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
