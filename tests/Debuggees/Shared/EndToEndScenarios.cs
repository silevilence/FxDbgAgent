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
            if (mode == "domains") { AppDomainScenarios.Run(directory); return; }
            if (mode == "gated")
            {
                File.WriteAllText(Path.Combine(directory, "ready"), Environment.GetEnvironmentVariable("FXDBG_MCP_TEST") + "|" + Environment.CurrentDirectory);
                Console.WriteLine("TARGET_STDOUT_MUST_NOT_ENTER_MCP");
                Console.Error.WriteLine("TARGET_STDERR_MUST_NOT_ENTER_MCP");
                DateTime deadline = DateTime.UtcNow.AddSeconds(45);
                while (!File.Exists(Path.Combine(directory, "go")))
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("MCP test gate was not opened.");
                    System.Threading.Thread.Sleep(10);
                }
            }
            if (mode == "runtime")
            {
                File.WriteAllText(Path.Combine(directory, "runtime-version"), "v" + Environment.Version);
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    foreach (System.Diagnostics.ProcessModule module in process.Modules)
                        if (string.Equals(module.ModuleName, "clr.dll", StringComparison.OrdinalIgnoreCase))
                            File.WriteAllText(Path.Combine(directory, "runtime-file-version"), module.FileVersionInfo.FileVersion);
                System.Threading.Thread.Sleep(60000);
                return;
            }
            if (mode == "paging") PagingScenario();
            else if (mode == "exception") ThrowScenario();
            else if (mode == "late")
            {
                Assembly module = Assembly.LoadFrom(Path.Combine(directory, "Fx40.LateModule.dll"));
                module.GetType("FxDbg.Debuggees.LateCode").GetMethod("Run").Invoke(null, null);
            }
            else Recurse(mode == "deep" ? 180 : 12);
            if (userCodeCalls != 0) throw new InvalidOperationException("Debugger evaluated target formatting code.");
            File.WriteAllText(Path.Combine(directory, "completed"), "ok");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Recurse(int depth)
        {
            if (depth != 0) return Recurse(depth - 1) + 1;
            var node = new Node();
            node.Self = node;
            string longText = new string('x', 32768);
            for (int index = 0; index < node.LargeTexts.Length; index++) node.LargeTexts[index] = longText;
            Observe(42, "hello-framework", node);
            return sink;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void PagingScenario()
        {
            var values = new int[100001];
            var graph = new PageNode();
            graph.Self = graph;
            for (int index = 0; index < values.Length; index++) values[index] = index;
            for (int index = 0; index < graph.Branches.Length; index++) graph.Branches[index] = new int[1001];
            sink = values.Length; // PAGING_BREAKPOINT
            sink++;
            GC.KeepAlive(values);
            GC.KeepAlive(graph);
        }

        private sealed class PageNode
        {
            public PageNode Self;
            public int[][] Branches = new int[101][];
            public string Text = new string('p', 40000);
            public int Dangerous { get { userCodeCalls++; throw new InvalidOperationException("Getter invoked"); } }
            public override string ToString() { userCodeCalls++; return "must-not-run"; }
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
            public object Nothing = null;
            public Node Self;
            public string Label = "node-label";
            public string[] LargeTexts = new string[129];
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
