using System.Globalization;

namespace FxDbg.Debuggees.Core80ConsoleX64;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--wait-milliseconds", StringComparison.Ordinal))
        {
            int waitMilliseconds = int.Parse(args[1], CultureInfo.InvariantCulture);
            Thread.Sleep(waitMilliseconds);
            return;
        }

        Console.WriteLine("FxDbg unsupported CoreCLR x64 debuggee");
    }
}
