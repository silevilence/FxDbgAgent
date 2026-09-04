using System;

namespace FxDbg.Debuggees.AnyCpuPreferred
{
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine(IntPtr.Size == 4 ? "x86" : "x64");
            return 0;
        }
    }
}
