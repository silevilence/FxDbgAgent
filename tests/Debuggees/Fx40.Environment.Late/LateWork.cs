using System;
using System.Runtime.CompilerServices;

namespace FxDbg.Debuggees.EnvironmentSetup
{
    public static class LateWork
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static int Run(int input)
        {
            int doubled = input * 2;
            int[] values = new[] { input, doubled };
            int result = doubled + 1; // ENV_LATE_BREAKPOINT
            GC.KeepAlive(input);
            GC.KeepAlive(doubled);
            GC.KeepAlive(values);
            return result;
        }
    }
}
