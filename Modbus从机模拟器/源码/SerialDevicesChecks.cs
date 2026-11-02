using System;
using System.Reflection;

// Standalone checks: reflection lets the same executable first demonstrate the
// missing API, then exercise the implementation without any hardware fixture.
internal static class SerialDevicesChecks
{
    private static int count;
    private static Type devices;
    private static Type device;

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        count++;
        Console.WriteLine("PASS " + message);
    }

    private static object Call(string method, params object[] arguments)
    {
        try { return devices.GetMethod(method).Invoke(null, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static object Descriptor(string port, string description)
    {
        object result = Activator.CreateInstance(device);
        device.GetProperty("PortName").SetValue(result, port, null);
        device.GetProperty("Description").SetValue(result, description, null);
        return result;
    }

    private static string Property(object value, string name)
    {
        return (string)device.GetProperty(name).GetValue(value, null);
    }

    public static int Main(string[] args)
    {
        try
        {
            devices = typeof(ModbusSimulator.SerialDevices).Assembly.GetType("ModbusSimulator.SerialDevices");
            device = typeof(ModbusSimulator.SerialDevices).Assembly.GetType("ModbusSimulator.SerialDevice");
            Check(devices != null && device != null, "serial device API exists");
            string[] accepted = { "COM3", "COM3: USB-SERIAL CH340", "COM3 \u00b7 USB-SERIAL CH340", " com3 " };
            foreach (string value in accepted)
                Check((string)Call("RawPort", value) == "COM3", "extract raw port: " + value);
            string[] invalid = { null, "", "COM0", "COM03", "COM-1", "COM3junk", "COM3/CH340", "CH340 (COM3)", "\\\\.\\COM3", "COM3:", "COM3 \u00b7 " };
            foreach (string value in invalid)
            {
                bool rejected = false;
                try { Call("RawPort", value); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "reject malformed port: " + (value ?? "<null>"));
            }
            object friendly = Descriptor("COM3", "USB-SERIAL CH340 (COM3)");
            Check(Property(friendly, "DisplayName") == "COM3: USB-SERIAL CH340", "friendly label retains COM and removes duplicate suffix");
            Check(friendly.ToString() == "COM3: USB-SERIAL CH340", "combobox text uses friendly label");
            Check(Property(Descriptor("COM4", null), "DisplayName") == "COM4", "missing description falls back to actual port");
            Array input = Array.CreateInstance(device, 8);
            input.SetValue(Descriptor("COM10", null), 0);
            input.SetValue(Descriptor("COM2", "COM2"), 1);
            input.SetValue(Descriptor("com2", "USB Serial Device (COM2)"), 2);
            input.SetValue(Descriptor("COM3", "USB-SERIAL CH340 (COM3)"), 3);
            input.SetValue(Descriptor("COM3", null), 4);
            input.SetValue(Descriptor("nonsense", "invalid"), 5);
            input.SetValue(null, 6);
            input.SetValue(Descriptor("COM1", "Communications Port (COM1)"), 7);
            Array result = (Array)Call("NormalizeDescriptors", new object[] { input });
            Check(result.Length == 4, "invalid entries removed and case-insensitive port duplicates merged");
            Check(Property(result.GetValue(0), "PortName") == "COM1" && Property(result.GetValue(1), "PortName") == "COM2" && Property(result.GetValue(2), "PortName") == "COM3" && Property(result.GetValue(3), "PortName") == "COM10", "COM ports sorted numerically");
            Check(Property(result.GetValue(1), "DisplayName") == "COM2: USB Serial Device", "friendly information survives duplicate raw entry");
            Check(Property(result.GetValue(3), "DisplayName") == "COM10", "missing driver information does not invent a device name");
            Check(Property(input.GetValue(2), "PortName") == "com2", "normalization leaves input objects unchanged");
            Check(((Array)Call("NormalizeDescriptors", new object[] { null })).Length == 0, "null descriptor source is empty");
            if (args.Length != 0 && args[0] == "--enumerate")
            {
                Array live = (Array)Call("Enumerate");
                Console.WriteLine("READ_ONLY_ENUMERATION " + live.Length + " ports (no port opened)");
                foreach (object item in live) Console.WriteLine("PORT " + item);
                foreach (object item in live)
                    Check((string)Call("RawPort", Property(item, "DisplayName")) == Property(item, "PortName"), "live display label resolves to its raw port");
            }
            Console.WriteLine("TOTAL " + count + ", FAILED 0");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
