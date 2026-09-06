using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FxDbg.Debuggees
{
    public sealed class DomainWorker : MarshalByRefObject
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void Run(string directory, string label)
        {
            var value = new DomainValue();
            value.Self = value;
            File.WriteAllText(Path.Combine(directory, "ready-" + label), AppDomain.CurrentDomain.Id.ToString());
            AppDomainScenarios.Wait(directory, "go-" + label);
            value.Number = 42; // DOMAIN_BREAKPOINT
            GC.KeepAlive(value);
            File.WriteAllText(Path.Combine(directory, "done-" + label), "done");
        }
        private sealed class DomainValue
        {
            public int Number = 7;
            public DomainValue Self;
        }
    }

    internal static class AppDomainScenarios
    {
        internal static void Run(string directory)
        {
            AppDomain first = AppDomain.CreateDomain("SameName");
            AppDomain second = AppDomain.CreateDomain("SameName");
            Thread a = Start(first, directory, "a");
            Thread b = Start(second, directory, "b");
            a.Join(); b.Join();
            AppDomain.Unload(first);
            AppDomain.Unload(second);
            AppDomain replacement = AppDomain.CreateDomain("SameName");
            Thread c = Start(replacement, directory, "c");
            c.Join();
            AppDomain.Unload(replacement);
            File.WriteAllText(Path.Combine(directory, "completed"), "ok");
        }

        private static Thread Start(AppDomain domain, string directory, string label)
        {
            var worker = (DomainWorker)domain.CreateInstanceAndUnwrap(typeof(DomainWorker).Assembly.FullName, typeof(DomainWorker).FullName);
            var thread = new Thread(() => worker.Run(directory, label));
            thread.Name = "DomainWorker-" + label;
            thread.Start();
            return thread;
        }

        internal static void Wait(string directory, string file)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(Path.Combine(directory, file)))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Domain test gate timed out.");
                Thread.Sleep(10);
            }
        }
    }
}
