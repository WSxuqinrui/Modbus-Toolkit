using System;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Collections;
class RawSendOrderChecks {
 static object Field(object o,string n){return o.GetType().GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o);}
 [STAThread] static int Main(string[] a){
  Assembly product=Assembly.LoadFrom(a[0]); Type ft=product.GetType("ModbusSimulator.MainForm"); Type st=product.GetType("ModbusSimulator.SimulatorServer");
  ThreadPool.SetMinThreads(32,32);
  using(Form form=(Form)Activator.CreateInstance(ft)){
   object bank=ft.GetProperty("Bank").GetValue(form,null); object server=Activator.CreateInstance(st,new object[]{bank,(byte)1,new Action<string>(delegate{})});
   ft.GetField("server",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,server);
   st.GetMethod("StartTcp").Invoke(server,new object[]{IPAddress.Loopback,0}); var listener=(TcpListener)Field(server,"listener");
   using(var peer=new TcpClient()){
    peer.Connect((IPEndPoint)listener.LocalEndpoint); peer.ReceiveTimeout=4000;
    string[] targets; do { Thread.Sleep(5); targets=(string[])st.GetMethod("GetRawTargets").Invoke(server,null); } while(targets.Length==0);
    object writeGate=null; foreach(object t in ((IDictionary)Field(server,"tcpTargets")).Values)writeGate=Field(t,"WriteGate");
    object panel=Field(form,"rawSender"); var send=(Func<string,byte[],bool>)panel.GetType().GetProperty("SendRequested").GetValue(panel,null);
    for(int trial=0;trial<5;trial++){
      Monitor.Enter(writeGate); try { for(int i=0;i<16;i++) { if(!send(targets[0],new byte[]{(byte)i}))throw new Exception("submit refused"); } Thread.Sleep(150); } finally{Monitor.Exit(writeGate);}
      byte[] received=new byte[16]; int offset=0; while(offset<16){int n=peer.GetStream().Read(received,offset,16-offset);if(n<=0)throw new EndOfStreamException(); offset+=n;}
      Console.WriteLine("trial "+trial+": "+BitConverter.ToString(received));
      bool ordered=true;for(int i=0;i<16;i++)if(received[i]!=i)ordered=false;
      while((int)Field(form,"pendingRawSends")!=0)Thread.Sleep(5);
      if(!ordered){Console.WriteLine("FAIL: accepted UI submissions arrived out of order");return 2;}
    }
   }
  } Console.WriteLine("PASS FIFO order across 5 rounds of 16 queued sends"); return 0;
 }
}