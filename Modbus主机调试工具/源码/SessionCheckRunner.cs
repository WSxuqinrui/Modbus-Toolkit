using System;
namespace ModbusMaster
{
    internal static class SessionCheckRunner
    {
        private static int Main()
        { try { Console.WriteLine("TOTAL "+MasterSessionChecks.Run()+", FAILED 0"); return 0; } catch(Exception ex) { Console.WriteLine("FAIL "+ex);return 1; } }
    }
}
