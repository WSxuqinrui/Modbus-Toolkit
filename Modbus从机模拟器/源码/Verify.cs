using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ModbusSimulator;

internal static class Verify
{
    private static int passed;
    private static byte[] Hex(string text) { return Array.ConvertAll(text.Split(' '), s => Convert.ToByte(s, 16)); }
    private static void Check(bool okay, string label) { if (!okay) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static void Expect(byte[] actual, string expected, string label)
    { Check(actual != null && BitConverter.ToString(actual).Replace('-', ' ') == expected, label); }
    private static byte[] Read(NetworkStream stream)
    {
        byte[] header = Exact(stream, 6);
        byte[] tail = Exact(stream, (header[4] << 8) | header[5]);
        byte[] frame = new byte[header.Length + tail.Length];
        Buffer.BlockCopy(header, 0, frame, 0, 6); Buffer.BlockCopy(tail, 0, frame, 6, tail.Length); return frame;
    }
    private static byte[] Exact(NetworkStream stream, int count)
    {
        byte[] bytes = new byte[count]; int i = 0;
        while (i < count) { int n = stream.Read(bytes, i, count - i); if (n == 0) throw new Exception("unexpected EOF"); i += n; }
        return bytes;
    }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            RegisterBank bank = new RegisterBank();
            using (SimulatorServer server = new SimulatorServer(bank, 1, null))
            {
                // 临时监听仅本机，不改变用户防火墙或占用标准 502 端口。
                int port = 15000 + System.Diagnostics.Process.GetCurrentProcess().Id % 10000;
                server.StartTcp(IPAddress.Loopback, port);
                using (TcpClient client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 2000;
                    NetworkStream stream = client.GetStream();
                    byte[] write = Hex("00 01 00 00 00 06 01 06 00 00 00 7B");
                    stream.Write(write, 0, 3); Thread.Sleep(25); stream.Write(write, 3, write.Length - 3);
                    Expect(Read(stream), "00 01 00 00 00 06 01 06 00 00 00 7B", "TCP fragmented FC06 and transaction echo");
                    byte[] pipeline = Hex("00 02 00 00 00 06 01 03 00 00 00 01 00 03 00 00 00 06 01 04 00 00 00 01");
                    stream.Write(pipeline, 0, pipeline.Length);
                    Expect(Read(stream), "00 02 00 00 00 05 01 03 02 00 7B", "TCP pipelined FC03 reads previous write");
                    Expect(Read(stream), "00 03 00 00 00 05 01 04 02 00 00", "TCP pipelined FC04 has separate storage");
                    byte[] badAddress = Hex("00 04 00 00 00 06 01 03 03 E7 00 02");
                    stream.Write(badAddress, 0, badAddress.Length);
                    Expect(Read(stream), "00 04 00 00 00 03 01 83 02", "TCP address exception");
                    byte[] differentUnit = Hex("00 05 00 00 00 06 02 06 00 00 00 FF 00 06 00 00 00 06 01 03 00 00 00 01");
                    stream.Write(differentUnit, 0, differentUnit.Length);
                    Expect(Read(stream), "00 06 00 00 00 05 01 03 02 00 7B", "mismatched unit neither writes nor replies");
                }
                using (TcpClient bad = new TcpClient())
                {
                    bad.Connect(IPAddress.Loopback, port); bad.ReceiveTimeout = 2000;
                    byte[] frame = Hex("00 01 00 01 00 06 01 03 00 00 00 01");
                    bad.GetStream().Write(frame, 0, frame.Length);
                    bool closed = false;
                    try { closed = bad.GetStream().ReadByte() == -1; }
                    catch (IOException ex)
                    {
                        SocketException socket = ex.InnerException as SocketException;
                        if (socket == null || socket.SocketErrorCode != SocketError.ConnectionReset) throw;
                        closed = true;
                    }
                    Check(closed, "invalid protocol ID closes connection");
                }
                bank.Set(2, 0, 0);
                Expect(server.ProcessRtu(Hex("01 03 00 00 00 01 84 0A")), "01 03 02 00 00 B8 44", "RTU request and reply known CRC");
                Check(server.ProcessRtu(Hex("01 03 00 00 00 01 84 0B")) == null, "RTU bad CRC ignored");
                byte[] broadcast = Hex("00 06 00 00 00 7B 00 00");
                ushort crc = RegisterBank.Crc(broadcast, 6); broadcast[6] = (byte)crc; broadcast[7] = (byte)(crc >> 8);
                Check(server.ProcessRtu(broadcast) == null && bank.Get(2, 0) == 123, "RTU broadcast writes without reply");
                Check(server.ProcessMbap(Hex("00 01 00 00 00 07 01 03 00 00 00 01")) == null, "MBAP inconsistent length rejected");
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm())
            {
                form.Show(); Application.DoEvents();
                Check(form.Controls.Count > 10, "native form starts");
                DataGridView grid = null;
                foreach (Control control in form.Controls) if (control is DataGridView) grid = (DataGridView)control;
                Check(grid != null && grid.Rows.Count == 100, "register table shows 100 editable addresses");
                BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                NumericUpDown uiPort = (NumericUpDown)typeof(MainForm).GetField("port", fields).GetValue(form);
                Button toggle = (Button)typeof(MainForm).GetField("toggle", fields).GetValue(form);
                uiPort.Value = 25000 + System.Diagnostics.Process.GetCurrentProcess().Id % 10000;
                grid.CurrentCell = grid.Rows[0].Cells[2]; grid.BeginEdit(true);
                ((TextBox)grid.EditingControl).Text = "321"; grid.EndEdit();
                toggle.PerformClick();
                Check(toggle.Text == "停止从机", "UI start button starts listener");
                using (TcpClient master = new TcpClient())
                {
                    master.Connect(IPAddress.Loopback, (int)uiPort.Value); master.ReceiveTimeout = 2000;
                    byte[] request = Hex("12 34 00 00 00 06 01 03 00 00 00 01");
                    master.GetStream().Write(request, 0, request.Length);
                    Expect(Read(master.GetStream()), "12 34 00 00 00 05 01 03 02 01 41", "master reads value edited in real grid");
                    byte[] change = Hex("12 35 00 00 00 06 01 06 00 00 03 E8");
                    master.GetStream().Write(change, 0, change.Length);
                    Expect(Read(master.GetStream()), "12 35 00 00 00 06 01 06 00 00 03 E8", "UI listener handles master write");
                    DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                    while (Convert.ToString(grid.Rows[0].Cells[2].Value) != "1000" && DateTime.UtcNow < deadline)
                    { Application.DoEvents(); Thread.Sleep(10); }
                    Check(Convert.ToString(grid.Rows[0].Cells[2].Value) == "1000", "master write automatically refreshes grid");
                }
                toggle.PerformClick();
                Check(toggle.Text == "启动从机", "UI stop button releases listener");
                using (Bitmap image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                    image.Save(Path.Combine(args[0], "界面预览.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close();
            }
            Console.WriteLine("TOTAL " + passed + ", FAILED 0"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
