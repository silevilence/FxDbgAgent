using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace FxDbg.Debuggees.EnvironmentSetup
{
    // Fixed actions for the loopback-only IIS fixture; never accepts code or paths.
    public static class Lifecycle
    {
        private static readonly Dictionary<string, AppDomain> domains = new Dictionary<string, AppDomain>();
        private static readonly Dictionary<string, DomainRunner> runners = new Dictionary<string, DomainRunner>();
        private static Assembly memory;
        private static int activeHolds;
        public static int ActiveHolds { get { return Interlocked.CompareExchange(ref activeHolds, 0, 0); } }

        public static int Execute(string action)
        {
            if (action == "hold")
            {
                Interlocked.Increment(ref activeHolds);
                try
                {
                    DateTime limit = DateTime.UtcNow.AddSeconds(40);
                    while (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "release.request")) && DateTime.UtcNow < limit)
                        Thread.Sleep(50);
                    return 85;
                }
                finally { Interlocked.Decrement(ref activeHolds); }
            }
            lock (domains)
            {
                if (action == "domains-create") { Create("a"); Create("b"); }
                if (action == "reset-a") { Remove("a"); Create("a"); }
                if (action == "domains-clear") { Remove("a"); Remove("b"); }
                if (action == "run-a") return runners["a"].Run();
                if (action == "run-b") return runners["b"].Run();
                if (action == "memory" && memory == null)
                {
                    AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("FxDbg.Stage34.Memory"), AssemblyBuilderAccess.Run);
                    ModuleBuilder module = assembly.DefineDynamicModule("FxDbg.Stage34.Memory");
                    TypeBuilder type = module.DefineType("MemoryWork", TypeAttributes.Public);
                    MethodBuilder method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
                    ILGenerator il = method.GetILGenerator(); il.Emit(OpCodes.Ldc_I4, 85); il.Emit(OpCodes.Ret);
                    int result = (int)type.CreateType().GetMethod("Run").Invoke(null, null);
                    memory = assembly;
                    return result;
                }
                return 0;
            }
        }

        public static Dictionary<string, int> Snapshot()
        {
            lock (domains)
            {
                var result = new Dictionary<string, int>();
                foreach (var pair in domains) result.Add(pair.Key, pair.Value.Id);
                return result;
            }
        }

        private static void Create(string key)
        {
            if (domains.ContainsKey(key)) return;
            string root = AppDomain.CurrentDomain.BaseDirectory;
            AppDomain domain = AppDomain.CreateDomain("FxDbgFixtureDomain", null, new AppDomainSetup
            { ApplicationBase = root, PrivateBinPath = "bin", ShadowCopyFiles = "true" });
            domains.Add(key, domain);
            var runner = (DomainRunner)domain.CreateInstanceFromAndUnwrap(typeof(DomainRunner).Assembly.Location, typeof(DomainRunner).FullName);
            runners.Add(key, runner);
            runner.Load();
        }

        private static void Remove(string key)
        {
            AppDomain domain;
            if (!domains.TryGetValue(key, out domain)) return;
            AppDomain.Unload(domain); domains.Remove(key); runners.Remove(key);
        }
    }

    public sealed class DomainRunner : MarshalByRefObject
    {
        private Assembly late;
        public void Load() { late = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "late", "Fx40.Environment.Late.dll")); }
        public int Run() { return (int)late.GetType("FxDbg.Debuggees.EnvironmentSetup.LateWork").GetMethod("Run").Invoke(null, new object[] { 42 }); }
        public override object InitializeLifetimeService() { return null; }
    }
}
