using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;

namespace FxDbg.Debuggees
{
    public sealed class Worker : MarshalByRefObject
    {
        public void Run(string directory, int cycle)
        {
            Assembly library = Assembly.LoadFrom(Path.Combine(directory, "Fx40.LateModule.dll"));
            File.WriteAllText(Path.Combine(directory, "loaded-" + cycle), "ready");
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(directory, "go-" + cycle)))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Test gate did not open.");
                Thread.Sleep(10);
            }
            object result = library.GetType("FxDbg.Debuggees.LateCode").GetMethod("Run").Invoke(null, null);
            if ((int)result != 42) throw new InvalidOperationException("Unexpected target result.");
        }
    }

    internal static class Program
    {
        private static int executionSink;
        private static int variableSink;
        private static int userCodeCalls;

        private static void Main(string[] args)
        {
            if (args[0] == "--evaluation")
            {
                var node = new Node(); node.Self = node;
                var matrix = new int[,] { { 10,20 }, { 30,40 } };
                Array shifted = Array.CreateInstance(typeof(int), new[] {2,3}, new[] {-2,5}); shifted.SetValue(99,-1,7);
                variableSink = Node.Counter;
                EvaluateTarget(42,"abcdefghijklmnop",node,matrix,shifted,9007199254740993L,new string('s',32769));
                if (userCodeCalls != 0 || Node.Counter != 777 || node.Label != "node-label" || node.Self != node ||
                    matrix[1,1] != 40 || (int)shifted.GetValue(-1,7) != 99 || variableSink != 42)
                    throw new InvalidOperationException("Evaluation changed target state.");
                if (args.Length > 1) File.WriteAllText(args[1],"ok");
                return;
            }
            if (args[0] == "--breakpoint-race") { RunBreakpointRace(args[1]); return; }
            if (args[0] == "--exceptions")
            {
                try { ThrowObserved("handled-message"); }
                catch (ObservedException) { }
                ThrowObserved("unhandled-message");
                return;
            }
            if (args[0] == "--variables")
            {
                var node = new Node();
                node.Self = node;
                var values = new int[100000];
                values[0] = 10;
                values[99999] = 909;
                variableSink = Node.Counter;
                ObserveVariables(42, "abcdefghijklmnop", node, values, null);
                if (userCodeCalls != 0) throw new InvalidOperationException("Debugger executed target formatting code.");
                return;
            }
            if (args[0] == "--execution") { RunExecution(args[1]); return; }
            for (int cycle = 0; cycle < 3; cycle++)
            {
                AppDomain domain = AppDomain.CreateDomain("LateModule-" + cycle);
                var worker = (Worker)domain.CreateInstanceAndUnwrap(typeof(Worker).Assembly.FullName, typeof(Worker).FullName);
                worker.Run(args[0], cycle);
                AppDomain.Unload(domain);
                File.WriteAllText(Path.Combine(args[0], "unloaded-" + cycle), "done");
            }
        }

        // This fixture isolates native stop counts and needs a bindable loop sequence point.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void RunBreakpointRace(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "race-ready"), "ready");
            for (int index = 0; index < 32; index++)
            {
                while (!File.Exists(Path.Combine(directory, "go-" + index))) Thread.Sleep(10);
                File.WriteAllText(Path.Combine(directory, "hit-" + index), "hit"); // RACE_BREAKPOINT
            }
            while (!File.Exists(Path.Combine(directory, "race-done"))) Thread.Sleep(10);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunExecution(string directory)
        {
            executionSink = AddOne(7); // STEP_CALL
            executionSink += 10; // STEP_AFTER
            executionSink = AddOne(executionSink); // STEP_OVER
            File.WriteAllText(Path.Combine(directory, "execution-ready"), executionSink.ToString()); // STEP_READY
            while (!File.Exists(Path.Combine(directory, "execution-done"))) Thread.Sleep(10);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AddOne(int value)
        { // STEP_INNER_ENTRY
            int result = value + 1; // STEP_INNER
            return result;
        }

        private sealed class Node
        {
            public static int Counter = 777;
            public Node Self;
            public string Label = "node-label";
            public object Empty = null;
            public int Dangerous { get { userCodeCalls++; throw new InvalidOperationException("Getter executed"); } }
            public override string ToString() { userCodeCalls++; return "FORMATTING_EXECUTED"; }
        }

        private sealed class ObservedException : Exception
        {
            internal ObservedException(string message) : base(message) { }
            public override string Message { get { userCodeCalls++; return "custom-message-must-not-run"; } }
            public override string ToString() { userCodeCalls++; return "custom-format-must-not-run"; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowObserved(string message)
        {
            throw new ObservedException(message); // EXCEPTION_THROW
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ObserveVariables(int number, string message, Node node, int[] values, object nothing)
        {
            int localNumber = number * 2;
            string localText = "local-text";
            int optimized = number * 3;
            variableSink = optimized;
            variableSink = number; // VARIABLE_BREAKPOINT
            GC.KeepAlive(localNumber);
            GC.KeepAlive(localText);
            GC.KeepAlive(number);
            GC.KeepAlive(message);
            GC.KeepAlive(node);
            GC.KeepAlive(values);
            GC.KeepAlive(nothing);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EvaluateTarget(int number, string message, Node node, int[,] matrix, Array shifted, long exact, string oversized)
        {
            int localNumber = number * 2;
            variableSink = number; // EVALUATION_BREAKPOINT
            if ((int)shifted.GetValue(-1,7) != 99 || oversized.Length != 32769) throw new InvalidOperationException("Evaluation arguments changed.");
            GC.KeepAlive(number); GC.KeepAlive(message); GC.KeepAlive(node); GC.KeepAlive(matrix);
            GC.KeepAlive(shifted); GC.KeepAlive(exact); GC.KeepAlive(oversized); GC.KeepAlive(localNumber);
        }
    }
}
