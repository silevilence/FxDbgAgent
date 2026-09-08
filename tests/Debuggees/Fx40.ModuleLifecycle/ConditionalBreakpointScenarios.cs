using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace FxDbg.Debuggees
{
    public sealed class ConditionalNode
    {
        public int Value = 7;
        public short Small = short.MinValue;
        public int Getter { get { ConditionalBreakpointScenarios.FormattingCalls++; return Value; } }
        public override string ToString() { ConditionalBreakpointScenarios.FormattingCalls++; return "side effect"; }
    }
    public static class ConditionalBreakpointScenarios
    {
        public static int FormattingCalls;
        public static int Sum;
        public static void Run(string oracle)
        {
            var node = new ConditionalNode();
            for (int iteration = 1; iteration <= 6; iteration++) Hit(iteration, node);
            if (Sum != 21 || node.Value != 7 || FormattingCalls != 0) throw new InvalidOperationException("Debugger changed target state.");
            File.WriteAllText(oracle,"ok");
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Hit(int iteration, ConditionalNode node)
        {
            GC.KeepAlive(node); // CONDITIONAL_HIT
            Sum += iteration;
            GC.KeepAlive(iteration);
        }
    }
}
