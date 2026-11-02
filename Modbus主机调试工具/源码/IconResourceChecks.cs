using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class IconResourceChecks
{
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("MODBUS_DISABLE_UI_STATE", "1");
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0])); string ns = assembly.GetType("ModbusSimulator.MainForm") != null ? "ModbusSimulator" : "ModbusMaster";
            using (Stream resource = assembly.GetManifestResourceStream("app.ico"))
            { if (resource == null || resource.Length < 1000) throw new Exception("embedded icon missing"); }
            using (Icon icon = (Icon)assembly.GetType(ns + ".AppAssets").GetMethod("LoadIcon").Invoke(null, null))
            { GC.Collect(); using (Bitmap bitmap = icon.ToBitmap()) { if (bitmap.Width < 16 || bitmap.Height < 16) throw new Exception("icon cannot render after stream close"); } }
            using (Form form = (Form)Activator.CreateInstance(assembly.GetType(ns + ".MainForm"), new object[] { null }))
            { if (form.Icon == null || form.Icon.Handle == IntPtr.Zero) throw new Exception("window icon missing"); }
            using (Icon shell = Icon.ExtractAssociatedIcon(Path.GetFullPath(args[0])))
            { if (shell == null) throw new Exception("executable shell icon missing"); }
            Console.WriteLine("PASS embedded icon, independent clone, window icon and executable shell icon"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
