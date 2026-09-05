using System.Runtime.CompilerServices;

namespace FxDbg.Debuggees
{
    public static class LateCode
    {
        private static int sink;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run()
        {
            sink = 42; // LATE_BREAKPOINT
            return sink;
        }
    }
}
