using System;
using System.Globalization;
using System.Threading;

namespace FxDbg.Debuggees.ConsoleX86
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length == 1 && string.Equals(args[0], "--probe", StringComparison.Ordinal))
            {
                return;
            }

            if (args.Length == 2 && string.Equals(args[0], "--wait-milliseconds", StringComparison.Ordinal))
            {
                int waitMilliseconds = int.Parse(args[1], CultureInfo.InvariantCulture);
                Thread.Sleep(waitMilliseconds);
                return;
            }

            Console.WriteLine("FxDbg .NET Framework 4.0 x86 debuggee");
            Console.WriteLine("CLR " + Environment.Version);
        }
    }
}
