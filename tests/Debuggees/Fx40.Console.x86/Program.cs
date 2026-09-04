using System;

namespace FxDbg.Debuggees.ConsoleX86
{
    internal static class Program
    {
        private static void Main()
        {
            Console.WriteLine("FxDbg .NET Framework 4.0 x86 debuggee");
            Console.WriteLine("CLR " + Environment.Version);
        }
    }
}
