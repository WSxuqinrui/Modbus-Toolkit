using System;
using System.IO;
using System.Reflection;

namespace ModbusMaster
{
    public static class MasterClientChecks
    {
        private static int passed, failed;
        private static void Check(string name, Action test)
        { try { test(); passed++; Console.WriteLine("PASS: " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL: " + name + " " + ex); } }
        private static void Equal(string expected, byte[] actual)
        { if (BitConverter.ToString(actual) != expected) throw new Exception("Expected " + expected + ", actual " + BitConverter.ToString(actual)); }
        private static void Equal(ushort[] expected, ushort[] actual)
        { if (actual.Length != expected.Length) throw new Exception("value count"); for (int i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) throw new Exception("value " + i); }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        // 使用反射可先对尚未实现的纯时钟 helper 跑 RED，而不以编译错误代替失败断言。
        private static object Quiet(int baud, string parity, string stop)
        {
            Type type = typeof(MasterClientChecks).Assembly.GetType("ModbusMaster.MasterRtuSilentInterval");
            if (type == null) throw new Exception("Mode1 missing RTU t3.5 quiet interval enforcement");
            return Activator.CreateInstance(type, new object[] { new MasterConnectionOptions { Mode = 1, Com = "COM1", Baud = baud, Parity = parity, StopBits = stop }, 1000000L });
        }
        private static object QuietCall(object timing, string method, params object[] args)
        {
            try { return timing.GetType().GetMethod(method).Invoke(timing, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
        private static long QuietRemaining(object timing, long now) { return (long)QuietCall(timing, "RemainingTicks", now); }
        public static int Main()
        {
            Check("FC01 zero-based address", delegate { Equal("01-12-34-00-09", MasterProtocol.ReadRequest(0, 0x1234, 9)); });
            Check("FC02 independent area", delegate { Equal("02-00-07-00-01", MasterProtocol.ReadRequest(1, 7, 1)); });
            Check("FC03 big-endian", delegate { Equal("03-12-34-00-02", MasterProtocol.ReadRequest(2, 0x1234, 2)); });
            Check("FC04 last protocol address", delegate { Equal("04-FF-FF-00-01", MasterProtocol.ReadRequest(3, 65535, 1)); });
            Check("FC05 logical values", delegate { Equal("05-00-03-FF-00", MasterProtocol.WriteSingle(0, 3, 1)); Equal("05-00-03-00-00", MasterProtocol.WriteSingle(0, 3, 0)); });
            Check("FC06 full word", delegate { Equal("06-00-02-AB-CD", MasterProtocol.WriteSingle(2, 2, 0xABCD)); });
            Check("FC0F LSB packed", delegate { Equal("0F-00-02-00-09-02-8D-01", MasterProtocol.WriteMultiple(0, 2, new ushort[] { 1, 0, 1, 1, 0, 0, 0, 1, 1 })); });
            Check("FC10 big-endian payload", delegate { Equal("10-00-04-00-02-04-12-34-AB-CD", MasterProtocol.WriteMultiple(2, 4, new ushort[] { 0x1234, 0xABCD })); });
            Check("bit unpack exact quantity", delegate { Equal(new ushort[] { 1, 0, 1, 1, 0, 0, 0, 1, 1 }, MasterProtocol.ParseRead(MasterProtocol.ReadRequest(0, 2, 9), new byte[] { 1, 2, 0x8D, 0xFF })); });
            Check("word response decode", delegate { Equal(new ushort[] { 0x1234, 0xABCD }, MasterProtocol.ParseRead(MasterProtocol.ReadRequest(2, 4, 2), new byte[] { 3, 4, 0x12, 0x34, 0xAB, 0xCD })); });
            Check("read bits 2000 boundary", delegate { MasterProtocol.ReadRequest(0, 0, 2000); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.ReadRequest(0, 0, 2001); }); });
            Check("read words 125 boundary", delegate { MasterProtocol.ReadRequest(2, 0, 125); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.ReadRequest(3, 0, 126); }); });
            Check("write bits 1968 boundary", delegate { MasterProtocol.WriteMultiple(0, 0, new ushort[1968]); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteMultiple(0, 0, new ushort[1969]); }); });
            Check("write words 123 boundary", delegate { MasterProtocol.WriteMultiple(2, 0, new ushort[123]); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteMultiple(2, 0, new ushort[124]); }); });
            Check("address overflow rejected", delegate { Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.ReadRequest(2, 65535, 2); }); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteMultiple(0, 65535, new ushort[2]); }); });
            Check("invalid area/count/address", delegate { Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.ReadRequest(4, 0, 1); }); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.ReadRequest(0, 0, 0); }); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.ReadRequest(0, -1, 1); }); });
            Check("read-only areas reject write", delegate { Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteSingle(1, 0, 1); }); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteMultiple(3, 0, new ushort[] { 1 }); }); });
            Check("invalid coil values rejected", delegate { Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteSingle(0, 0, 2); }); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteMultiple(0, 0, new ushort[] { 2 }); }); });
            Check("null/empty multiple values", delegate { Throws<ArgumentNullException>(delegate { MasterProtocol.WriteMultiple(2, 0, null); }); Throws<ArgumentOutOfRangeException>(delegate { MasterProtocol.WriteMultiple(2, 0, new ushort[0]); }); });
            Check("short and excess read data rejected", delegate { byte[] q = MasterProtocol.ReadRequest(2, 0, 1); Throws<IOException>(delegate { MasterProtocol.ParseRead(q, new byte[] { 3 }); }); Throws<IOException>(delegate { MasterProtocol.ParseRead(q, new byte[] { 3, 2, 0 }); }); Throws<IOException>(delegate { MasterProtocol.ParseRead(q, new byte[] { 3, 2, 0, 1, 2 }); }); });
            Check("wrong read FC rejected", delegate { Throws<IOException>(delegate { MasterProtocol.ParseRead(MasterProtocol.ReadRequest(2, 0, 1), new byte[] { 4, 2, 0, 1 }); }); });
            Check("wrong byte count rejected", delegate { Throws<IOException>(delegate { MasterProtocol.ParseRead(MasterProtocol.ReadRequest(2, 0, 1), new byte[] { 3, 1, 0, 1 }); }); });
            Check("remote exception code preserved", delegate { try { MasterProtocol.ParseRead(MasterProtocol.ReadRequest(2, 0, 1), new byte[] { 0x83, 2 }); } catch (ModbusRemoteException ex) { if (ex.ExceptionCode == 2) return; } throw new Exception("remote code"); });
            Check("malformed exception rejected", delegate { Throws<IOException>(delegate { MasterProtocol.ParseRead(MasterProtocol.ReadRequest(2, 0, 1), new byte[] { 0x83 }); }); });
            Check("single write echo validated", delegate { byte[] q = MasterProtocol.WriteSingle(2, 2, 123); MasterProtocol.ValidateWrite(q, (byte[])q.Clone()); Throws<IOException>(delegate { MasterProtocol.ValidateWrite(q, new byte[] { 6, 0, 2, 0, 124 }); }); });
            Check("multiple write ack validated", delegate { byte[] q = MasterProtocol.WriteMultiple(2, 4, new ushort[] { 1, 2 }); MasterProtocol.ValidateWrite(q, new byte[] { 16, 0, 4, 0, 2 }); Throws<IOException>(delegate { MasterProtocol.ValidateWrite(q, new byte[] { 16, 0, 4, 0, 1 }); }); });
            Check("remote write exception", delegate { Throws<ModbusRemoteException>(delegate { MasterProtocol.ValidateWrite(MasterProtocol.WriteSingle(2, 0, 1), new byte[] { 0x86, 3 }); }); });
            Check("invalid request validation", delegate { Throws<ArgumentException>(delegate { MasterProtocol.ParseRead(new byte[] { 3 }, new byte[] { 3, 2, 0, 1 }); }); Throws<ArgumentException>(delegate { MasterProtocol.ValidateWrite(new byte[] { 3, 0, 0, 0, 1 }, new byte[5]); }); });
            Check("network IPv4/IPv6 accepted", delegate { new MasterConnectionOptions().Validate(); new MasterConnectionOptions { Host = "::1" }.Validate(); });
            Check("host/port/mode strict validation", delegate { Throws<ArgumentException>(delegate { new MasterConnectionOptions { Host = "localhost" }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Port = 0 }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Mode = 7 }.Validate(); }); });
            Check("timeout/unit strict validation", delegate { Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { TimeoutMs = 99 }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { TimeoutMs = 60001 }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Unit = 0 }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Unit = 248 }.Validate(); }); });
            Check("serial friendly label only normalized", delegate { new MasterConnectionOptions { Mode = 1, Com = "COM12: USB test" }.Validate(); Throws<ArgumentException>(delegate { new MasterConnectionOptions { Mode = 1, Com = "invalid" }.Validate(); }); });
            Check("7bit ASCII only", delegate { new MasterConnectionOptions { Mode = 3, Com = "COM1", DataBits = 7 }.Validate(); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Mode = 1, Com = "COM1", DataBits = 7 }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Mode = 2, Com = "COM1", DataBits = 7 }.Validate(); }); });
            Check("serial framing validation", delegate { Throws<ArgumentException>(delegate { new MasterConnectionOptions { Mode = 1, Com = "COM1", Parity = "invalid" }.Validate(); }); Throws<ArgumentException>(delegate { new MasterConnectionOptions { Mode = 1, Com = "COM1", StopBits = "0" }.Validate(); }); Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Mode = 1, Com = "COM1", Baud = 0 }.Validate(); }); });
            Check("persisted serial parameters must be representable in UI", delegate
            {
                foreach (int mode in new int[] { 0, 1, 2, 3, 4, 5, 6 })
                {
                    int selected = mode;
                    Throws<ArgumentException>(delegate { new MasterConnectionOptions { Mode = selected, Com = "COM1", Parity = "Mark" }.Validate(); });
                    Throws<ArgumentException>(delegate { new MasterConnectionOptions { Mode = selected, Com = "COM1", Parity = "Space" }.Validate(); });
                    Throws<ArgumentException>(delegate { new MasterConnectionOptions { Mode = selected, Com = "COM1", StopBits = "1.5" }.Validate(); });
                    Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Mode = selected, Com = "COM1", Baud = 12345 }.Validate(); });
                    Throws<ArgumentOutOfRangeException>(delegate { new MasterConnectionOptions { Mode = selected, Com = "COM1", DataBits = 9 }.Validate(); });
                }
                foreach (int baud in new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }) new MasterConnectionOptions { Baud = baud }.Validate();
            });
            Check("ASCII LRC decode and corruption", delegate { byte[] frame = AsciiProtocol.Encode(1, new byte[] { 3, 2, 0, 7 }); byte unit; byte[] pdu; if (!AsciiProtocol.TryDecode(frame, out unit, out pdu) || unit != 1) throw new Exception("ASCII decode"); frame[4] = (byte)'4'; if (AsciiProtocol.TryDecode(frame, out unit, out pdu)) throw new Exception("LRC accepted"); });
            Check("RTU fixed fixture CRC", delegate { byte unit; byte[] pdu; byte[] frame = new byte[] { 1, 3, 2, 0, 1, 0x79, 0x84 }; if (!ModbusMasterClient.TryDecodeRtu(frame, out unit, out pdu)) throw new Exception("known CRC"); Equal("01-03-00-00-00-0A-C5-CD", ModbusMasterClient.EncodeRtu(1, new byte[] { 3, 0, 0, 0, 10 })); frame[6] ^= 1; if (ModbusMasterClient.TryDecodeRtu(frame, out unit, out pdu)) throw new Exception("CRC accepted"); });
            Check("RTU low-speed t3.5 uses actual data/parity/stop bits", delegate
            {
                int[] bauds = { 1200, 9600, 9600, 9600, 19200, 38400, 115200 };
                string[] parity = { "None", "None", "Even", "None", "None", "None", "Even" };
                string[] stop = { "1", "1", "1", "2", "1", "1", "2" };
                long[] expected = { 29167, 3646, 4011, 4011, 1823, 1750, 1750 };
                for (int i = 0; i < bauds.Length; i++) { object timing = Quiet(bauds[i], parity[i], stop[i]); QuietCall(timing, "RecordReceive", 100000L); if (QuietRemaining(timing, 100000) != expected[i]) throw new Exception("t3.5 configuration " + i); }
            });
            Check("RTU queued TX includes serialization and last RX replaces estimate", delegate
            {
                object timing = Quiet(9600, "None", "1");
                QuietCall(timing, "RecordTransmitQueued", 100000L, 8);
                if (QuietRemaining(timing, 100000) != 11980 || QuietRemaining(timing, 111980) != 0) throw new Exception("queued TX drain estimate");
                QuietCall(timing, "RecordReceive", 101000L);
                if (QuietRemaining(timing, 101000) != 3646 || QuietRemaining(timing, 104646) != 0) throw new Exception("last RX did not supersede TX estimate");
                QuietCall(timing, "RecordReceive", 102000L);
                if (QuietRemaining(timing, 104646) != 1000) throw new Exception("each RX must restart silence");
            });
            Check("RTU wait consumes only missing quiet interval", delegate
            {
                object timing = Quiet(9600, "None", "1"); QuietCall(timing, "RecordReceive", 100000L); long now = 103000; int waits = 0;
                QuietCall(timing, "Wait", (Func<long>)delegate { return now; }, (Func<int>)delegate { return 100; }, (Func<int, bool>)delegate(int milliseconds) { waits++; now += milliseconds * 1000; return false; }, null);
                if (waits != 1 || now != 104000) throw new Exception("missing quiet interval wait");
                QuietCall(timing, "Wait", (Func<long>)delegate { return now; }, (Func<int>)delegate { return 100; }, (Func<int, bool>)delegate(int milliseconds) { throw new Exception("already quiet bus was delayed"); }, null);
            });
            Check("RTU silence shares total transaction deadline", delegate
            {
                object timing = Quiet(9600, "None", "1"); QuietCall(timing, "RecordReceive", 0L); long now = 0;
                Throws<TimeoutException>(delegate { QuietCall(timing, "Wait", (Func<long>)delegate { return now; }, (Func<int>)delegate { int remaining = 2 - (int)(now / 1000); if (remaining <= 0) throw new TimeoutException(); return remaining; }, (Func<int, bool>)delegate(int milliseconds) { now += milliseconds * 1000; return false; }, null); });
                if (now != 2000) throw new Exception("wait exceeded deadline budget");
            });
            Check("RTU quiet wait supports cancellation", delegate
            {
                object timing = Quiet(1200, "None", "1"); QuietCall(timing, "RecordReceive", 0L);
                Throws<ObjectDisposedException>(delegate { QuietCall(timing, "Wait", (Func<long>)delegate { return 0; }, (Func<int>)delegate { return 100; }, (Func<int, bool>)delegate(int milliseconds) { return true; }, null); });
            });
            Check("RTU late RX during quiet wait restarts interval", delegate
            {
                object timing = Quiet(9600, "None", "1"); QuietCall(timing, "RecordReceive", 100000L); long now = 103000; int polls = 0, waited = 0;
                QuietCall(timing, "Wait", (Func<long>)delegate { return now; }, (Func<int>)delegate { return 100; }, (Func<int, bool>)delegate(int milliseconds) { waited += milliseconds; now += milliseconds * 1000; return false; }, (Action)delegate { if (polls++ == 0) QuietCall(timing, "RecordReceive", now); });
                if (waited != 4 || now != 107000) throw new Exception("late RX failed to extend silence");
            });
            Console.WriteLine("MasterClientChecks: " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
}
