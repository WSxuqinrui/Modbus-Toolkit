using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ModbusSimulator;
internal static class VerifyNetworkUiChecks
{
    private static int passed;
    private static T Field<T>(object source, string name) { return (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source); }
    private static byte[] Hex(string text) { return Array.ConvertAll(text.Split(' '), b => Convert.ToByte(b, 16)); }
    private static void Check(bool okay, string text) { if (!okay) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
    private static void Equal(byte[] actual, string expected, string label) { Check(BitConverter.ToString(actual).Replace('-', ' ') == expected, label); }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm())
            {
                form.Show(); Application.DoEvents();
                ComboBox mode = Field<ComboBox>(form, "mode"); NumericUpDown port = Field<NumericUpDown>(form, "port");
                Field<TextBox>(form, "ip").Text = "127.0.0.1"; Button start = Field<Button>(form, "toggle");
                int basePort = 20000 + System.Diagnostics.Process.GetCurrentProcess().Id % 10000;
                for (int index = 4; index <= 6; index++)
                {
                    mode.SelectedIndex = index; port.Value = basePort + index;
                    Check(Field<TextBox>(form, "ip").Visible && port.Enabled && !Field<ComboBox>(form, "baud").Enabled, "network controls active for mode " + index);
                    Check(form.CaptureConfiguration().Mode == index, "network mode saved in configuration " + index);
                    start.PerformClick(); Check(start.Text == "停止从机", "start network listener from UI " + index);
                    if (index == 5)
                    {
                        using (TcpClient client = new TcpClient())
                        {
                            client.Connect(IPAddress.Loopback, (int)port.Value); client.ReceiveTimeout = 2000;
                            byte[] request = Hex("01 03 00 00 00 01 84 0A");
                            client.GetStream().Write(request, 0, 3); Thread.Sleep(15); client.GetStream().Write(request, 3, request.Length - 3);
                            byte[] response = new byte[7]; int offset = 0;
                            while (offset < response.Length) { int n = client.GetStream().Read(response, offset, response.Length - offset); if (n == 0) throw new Exception("early EOF"); offset += n; }
                            Equal(response, "01 03 02 00 00 B8 44", "RTU-over-TCP raw CRC reply through UI listener");
                        }
                    }
                    else
                    {
                        using (UdpClient client = new UdpClient())
                        {
                            client.Client.ReceiveTimeout = 2000; client.Connect(IPAddress.Loopback, (int)port.Value);
                            byte[] request = index == 4 ? Hex("12 34 00 00 00 06 01 03 00 00 00 01") : Hex("01 03 00 00 00 01 84 0A");
                            client.Send(request, request.Length); IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0); byte[] response = client.Receive(ref peer);
                            Equal(response, index == 4 ? "12 34 00 00 00 05 01 03 02 00 00" : "01 03 02 00 00 B8 44", "UDP response through UI mode " + index);
                        }
                    }
                    Application.DoEvents(); start.PerformClick(); Check(start.Text == "启动从机" && port.Enabled, "stop releases network mode " + index);
                }
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(args[0], "2.1网络模式.png"), System.Drawing.Imaging.ImageFormat.Png); }
                form.Close();
            }
            Console.WriteLine("TOTAL " + passed + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
