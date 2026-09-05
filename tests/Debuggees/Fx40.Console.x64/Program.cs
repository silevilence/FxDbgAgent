using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FxDbg.Debuggees.ConsoleX64
{
    internal static class Program
    {
        private static bool breakForNativeDebugger;
        private static volatile int breakpointValueSink;

        private static void Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--scenario") { EndToEndScenarios.Run(args[1], args[2]); return; }
            if (args.Length == 1 && (args[0] == "--stack-threads" || args[0] == "--stack-threads-windbg"))
            {
                Thread.CurrentThread.Name = "FxDbg-main";
                breakForNativeDebugger = args[0] == "--stack-threads-windbg";
                using (var finished = new ManualResetEvent(false))
                using (var ready = new ManualResetEvent(false))
                {
                    var worker = new Thread(() => { ready.Set(); finished.WaitOne(); });
                    worker.Name = "FxDbg-worker";
                    worker.Start();
                    ready.WaitOne();
                    StackLevel01();
                    finished.Set();
                    worker.Join();
                }
                return;
            }

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

            if (args.Length == 1 && string.Equals(args[0], "--breakpoint-probe", StringComparison.Ordinal))
            {
                StackLevel01();
                return;
            }

            if (args.Length == 1 && string.Equals(args[0], "--windbg-probe", StringComparison.Ordinal))
            {
                breakForNativeDebugger = true;
                StackLevel01();
                return;
            }

            Console.WriteLine("FxDbg .NET Framework 4.0 x64 debuggee");
            Console.WriteLine("CLR " + Environment.Version);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel01() { return StackLevel02() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel02() { return StackLevel03() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel03() { return StackLevel04() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel04() { return StackLevel05() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel05() { return StackLevel06() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel06() { return StackLevel07() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel07() { return StackLevel08() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel08() { return StackLevel09() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel09() { return StackLevel10() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel10() { return StackLevel11() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel11() { return StackLevel12() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int StackLevel12() { return BreakpointTarget() + 1; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int BreakpointTarget()
        {
            if (breakForNativeDebugger)
            {
                Debugger.Break();
            }

            breakpointValueSink = 1;
            return breakpointValueSink;
        }
    }
}
