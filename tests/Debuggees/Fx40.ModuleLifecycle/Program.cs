using System;
using System.IO;
using System.Reflection;
using System.Threading;

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
        private static void Main(string[] args)
        {
            for (int cycle = 0; cycle < 3; cycle++)
            {
                AppDomain domain = AppDomain.CreateDomain("LateModule-" + cycle);
                var worker = (Worker)domain.CreateInstanceAndUnwrap(typeof(Worker).Assembly.FullName, typeof(Worker).FullName);
                worker.Run(args[0], cycle);
                AppDomain.Unload(domain);
                File.WriteAllText(Path.Combine(args[0], "unloaded-" + cycle), "done");
            }
        }
    }
}
