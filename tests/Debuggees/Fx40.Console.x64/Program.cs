using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FxDbg.Debuggees.ConsoleX64
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

            if (args.Length == 3 && string.Equals(args[0], "--launch-observation", StringComparison.Ordinal))
            {
                File.WriteAllText(
                    args[1],
                    Environment.GetEnvironmentVariable("FXDBG_TEST") + "|" +
                    Environment.CurrentDirectory + "|" +
                    args[2]);
                return;
            }

            Console.WriteLine("FxDbg .NET Framework 4.0 x64 debuggee");
            Console.WriteLine("CLR " + Environment.Version);
        }
    }
}
