using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Slave = ModbusSimulator;
using Host = ModbusMaster;
internal static class NewUi27Checks
{
    private static int checks;
    private static T Field<T>(object obj,string name){return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(obj);}
    private static void Check(bool yes,string text){if(!yes)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    private static void Wait(Func<bool> ready,string text){Stopwatch watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(5);}Check(ready(),text);}
    private static int FreePort(){TcpListener listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private static byte[] Read(NetworkStream stream,int count){byte[] data=new byte[count];int offset=0;while(offset<count){int n=stream.Read(data,offset,count-offset);if(n==0)throw new EndOfStreamException();offset+=n;}return data;}
    private static void Key(Control control,Keys key){typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(control,new object[]{new KeyEventArgs(key)});}
    private static Host.AddressCard Card(Host.CardView view,int area,int address){view.Locate(area,address);foreach(Host.AddressCard card in view.Cards)if(card.AreaId==area&&card.Address==address)return card;throw new Exception("card not found");}
    private static void Capture(Form form,string file){Application.DoEvents();using(Bitmap b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(file,System.Drawing.Imaging.ImageFormat.Png);}}
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(Slave.MainForm slave=new Slave.MainForm())
            using(Host.MainForm master=new Host.MainForm())
            {
                slave.Show();master.Show();int port=FreePort();Slave.SimulatorConfiguration scfg=slave.CaptureConfiguration();scfg.Ip="127.0.0.1";scfg.Port=port;slave.ApplyConfiguration(scfg);slave.Bank.Set(2,0,100);
                Field<Button>(slave,"toggle").PerformClick();Wait(delegate{return Field<Slave.SimulatorServer>(slave,"server")!=null;},"slave UI starts selected loopback server");
                Host.MasterConfiguration cfg=master.CaptureConfiguration();cfg.Connection.Host="127.0.0.1";cfg.Connection.Port=port;cfg.Display.Port=port;cfg.Connection.TimeoutMs=700;master.ApplyConfiguration(cfg);master.Connect();
                Wait(delegate{return master.Session!=null&&master.Session.IsConnected&&master.Bank.Get(2,0)==100;},"master actual connection and initial read");
                Field<ComboBox>(master,"displayMode").SelectedIndex=4;Host.AddressCard card=Card(master.Views.Cards,2,0);
                Check(card.BeginValueEdit(),"connected holding card opens inline editor");card.QuickValueInput.Text="222";Key(card.QuickValueInput,Keys.Enter);
                Wait(delegate{return slave.Bank.Get(2,0)==222&&master.Bank.Get(2,0)==222;},"inline value submits remote write then reads back");
                card.NameInput.Text="压力值";card.UnitInput.Text="kPa";card.SaveLabel();Field<ComboBox>(master,"displayMode").SelectedIndex=0;Application.DoEvents();
                Check(Convert.ToString(master.Views.ListGrid["Name",0].Value)=="压力值"&&Convert.ToString(master.Views.ListGrid["Unit",0].Value)=="kPa","card metadata appears immediately in combined list");
                master.Views.ListGrid.CurrentCell=master.Views.ListGrid["Name",0];Check(master.Views.ListGrid.BeginEdit(true),"name cell begins editing while connected");((TextBox)master.Views.ListGrid.EditingControl).Text="压力同步";Check(master.Views.Commit(),"name cell commits metadata");
                Field<ComboBox>(master,"displayMode").SelectedIndex=4;card=Card(master.Views.Cards,2,0);Check(card.Header.Text.Contains("压力同步"),"list rename synchronizes to card header");
                master.SetPolling(true);master.rawSender.RefreshTargets();master.rawSender.InputBox.Text="00 55 00 00 00 06 01 06 00 00 01 4D";master.rawSender.ChecksumCombo.SelectedIndex=0;master.rawSender.NewlineCombo.SelectedIndex=0;master.rawSender.SendButton.PerformClick();
                Wait(delegate{return master.Session.IsRawMode&&!master.Session.IsBusy&&slave.Bank.Get(2,0)==333;},"custom master bytes sent unchanged and raw response window completed");
                Check(!master.Session.IsPolling&&master.Bank.Get(2,0)==222,"raw mode stops polling and does not parse reply into cache");
                Check(!Field<Button>(master,"readOnce").Enabled&&!master.Areas[2].CanWrite,"typed UI writes and reads disabled in raw mode");
                Slave.SimulatorServer cycleServer=Field<Slave.SimulatorServer>(slave,"server"); long beforeCycle=cycleServer.Requests;
                master.rawSender.PeriodInput.Value=50; master.rawSender.CycleCheck.Checked=true;
                Wait(delegate{return cycleServer.Requests>=beforeCycle+3;},"actual master cycle uses the connected TCP channel repeatedly");
                master.rawSender.CycleCheck.Checked=false;
                Wait(delegate{return !master.Session.HasPendingWork;},"cycle stop drains the last in-flight transaction");
                long stoppedRequests=cycleServer.Requests; DateTime stopCheck=DateTime.UtcNow.AddMilliseconds(180); while(DateTime.UtcNow<stopCheck){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check(cycleServer.Requests==stoppedRequests&&master.Bank.Get(2,0)==222,"stopping cycle prevents new packets and raw replies never alter cache");
                bool rejected=false;try{master.ReadOnce();}catch(InvalidOperationException){rejected=true;}Check(rejected,"typed read API also rejects raw-mode mixing");
                using(TcpClient peer=new TcpClient())
                {
                    peer.Connect(IPAddress.Loopback,port);peer.ReceiveTimeout=1500;
                    Slave.SimulatorServer server=Field<Slave.SimulatorServer>(slave,"server");Wait(delegate{return server.GetRawTargets().Length==2&&slave.rawSender.Enabled;},"slave custom panel observes two real peers");
                    slave.rawSender.RefreshTargets();string endpoint=peer.Client.LocalEndPoint.ToString();string target=null;foreach(object item in slave.rawSender.TargetCombo.Items)if(item.ToString().Contains(endpoint))target=item.ToString();Check(target!=null,"raw target identifies specific receiving peer");
                    slave.rawSender.TargetCombo.SelectedItem=target;slave.rawSender.InputBox.Text="01 03 00 00 00 01";slave.rawSender.ChecksumCombo.SelectedIndex=1;slave.rawSender.ByteOrderCombo.SelectedIndex=0;slave.rawSender.NewlineCombo.SelectedIndex=3;slave.rawSender.SendButton.PerformClick();
                    Check(BitConverter.ToString(Read(peer.GetStream(),10))=="01-03-00-00-00-01-84-0A-0D-0A","slave sends Modbus CRC and CRLF exactly once");
                    slave.rawSender.AsciiRadio.Checked=true;slave.rawSender.ChecksumCombo.SelectedIndex=0;slave.rawSender.NewlineCombo.SelectedIndex=2;slave.rawSender.InputBox.Text="HELLO";slave.rawSender.SendButton.PerformClick();
                    Check(System.Text.Encoding.ASCII.GetString(Read(peer.GetStream(),6))=="HELLO\n","slave ASCII custom send uses selected newline");
                }
                master.Disconnect();master.Connect();Wait(delegate{return master.Session!=null&&master.Session.IsConnected&&!master.Session.IsRawMode&&master.Bank.Get(2,0)==333;},"reconnect leaves raw mode and confirms device value");
                Field<ComboBox>(master,"displayMode").SelectedIndex=4;card=Card(master.Views.Cards,2,0);if(card.IsExpanded)card.Header.PerformClick();card.BeginValueEdit();card.QuickValueInput.Text="444";
                Capture(master,Path.Combine(args[0],"主机1.2快速编辑与自定义发送.png"));Key(card.QuickValueInput,Keys.Escape);
                Field<ComboBox>(slave,"displayMode").SelectedIndex=4;slave.Views.Cards.Locate(0,0);Slave.AddressCard expanded=null;foreach(Slave.AddressCard item in slave.Views.Cards.Cards)if(item.IsExpanded)expanded=item;if(expanded!=null)expanded.Header.PerformClick();
                Capture(slave,Path.Combine(args[0],"从机2.7卡片和自定义发送.png"));
                master.Size=master.MinimumSize;Application.DoEvents();Check(master.rawSender.InputBox.Height>=40&&master.rawSender.SendButton.Right<=master.rawSender.ClientSize.Width,"raw send controls fit minimum window");Capture(master,Path.Combine(args[0],"主机1.2最小窗口.png"));
                master.Disconnect();Field<Button>(slave,"toggle").PerformClick();master.Close();slave.Close();
            }
            Console.WriteLine("TOTAL "+checks+", FAILED 0");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
