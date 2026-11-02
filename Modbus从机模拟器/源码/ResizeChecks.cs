using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
internal static class ResizeChecks
{
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Assembly assembly = Assembly.LoadFrom(args[0]);
        using (Form form = (Form)Activator.CreateInstance(assembly.GetType("ModbusSimulator.MainForm")))
        {
            form.Show(); Application.DoEvents();
            MethodInfo begin = typeof(Form).GetMethod("OnResizeBegin", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo end = typeof(Form).GetMethod("OnResizeEnd", BindingFlags.Instance | BindingFlags.NonPublic);
            begin.Invoke(form, new object[] { EventArgs.Empty }); Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) { form.Size = new Size(1000 + i % 10 * 24, 720 + i % 10 * 12); Application.DoEvents(); }
            end.Invoke(form, new object[] { EventArgs.Empty }); clock.Stop();
            Console.WriteLine("50 resize cycles including layout and message pump: " + clock.ElapsedMilliseconds + " ms");
            form.Close();
        }
        return 0;
    }
}
