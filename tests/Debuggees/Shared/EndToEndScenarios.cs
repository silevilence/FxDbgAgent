using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FxDbg.Debuggees
{
    internal static class EndToEndScenarios
    {
        private static int sink;
        private static int userCodeCalls;

        internal static void Run(string mode, string directory)
        {
            if (mode == "exception") ThrowScenario();
            else if (mode == "late")
            {
                Assembly module = Assembly.LoadFrom(Path.Combine(directory, "Fx40.LateModule.dll"));
                module.GetType("FxDbg.Debuggees.LateCode").GetMethod("Run").Invoke(null, null);
            }
            else Recurse(12);
            if (userCodeCalls != 0) throw new InvalidOperationException("Debugger evaluated target formatting code.");
            File.WriteAllText(Path.Combine(directory, "completed"), "ok");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Recurse(int depth)
        {
            if (depth != 0) return Recurse(depth - 1) + 1;
            var node = new Node();
            node.Self = node;
            Observe(42, "hello-framework", node);
            return sink;
        }

        // Keep the basic-local acceptance fixture observable in both build configurations.
        // Optimized-away values are covered separately by the native variable suite.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void Observe(int number, string message, Node node)
        {
            int localNumber = number * 2;
            string localText = "local-value";
            sink = number; // E2E_BREAKPOINT
            sink = AddOne(number); // E2E_STEP_CALL
            sink += localNumber; // E2E_STEP_AFTER
            GC.KeepAlive(localNumber);
            GC.KeepAlive(localText);
            GC.KeepAlive(number);
            GC.KeepAlive(message);
            GC.KeepAlive(node);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AddOne(int value)
        {
            return value + 1; // E2E_STEP_INNER
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowScenario()
        {
            throw new ScenarioException("e2e-unhandled-message"); // E2E_EXCEPTION
        }

        private sealed class Node
        {
            public Node Self;
            public string Label = "node-label";
            public int Dangerous { get { userCodeCalls++; throw new InvalidOperationException("Getter invoked"); } }
            public override string ToString() { userCodeCalls++; return "must-not-run"; }
        }

        private sealed class ScenarioException : Exception
        {
            internal ScenarioException(string message) : base(message) { }
            public override string Message { get { userCodeCalls++; return "custom-message"; } }
            public override string ToString() { userCodeCalls++; return "custom-format"; }
        }
    }
}
