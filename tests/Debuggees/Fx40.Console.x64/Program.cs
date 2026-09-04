using System;

namespace FxDbg.Debuggees.ConsoleX64
{
    internal static class Program
    {
        private static void Main()
        {
            Console.WriteLine("FxDbg .NET Framework 4.0 x64 debuggee");
            Console.WriteLine("CLR " + Environment.Version);
        }
    }
}
