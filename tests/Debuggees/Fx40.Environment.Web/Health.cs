using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Web;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;

namespace FxDbg.Debuggees.EnvironmentSetup
{
    public static class Health
    {
        public static string Read()
        {
            string action = HttpContext.Current.Request.QueryString["action"];
            int lateResult = 0;
            if (action == "late")
            {
                Assembly late = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "late", "Fx40.Environment.Late.dll"));
                lateResult = (int)late.GetType("FxDbg.Debuggees.EnvironmentSetup.LateWork").GetMethod("Run").Invoke(null, new object[] { 42 });
            }
            using (Process process = Process.GetCurrentProcess())
            {
                Assembly assembly = typeof(Health).Assembly;
                return new JavaScriptSerializer().Serialize(new
                {
                    kind = "FxDbg-stage3-4-environment",
                    pid = process.Id,
                    bits = IntPtr.Size * 8,
                    runtime = Environment.Version.ToString(),
                    appDomainId = AppDomain.CurrentDomain.Id,
                    shadowCopy = AppDomain.CurrentDomain.ShadowCopyFiles,
                    assemblyPath = assembly.Location,
                    originalAssembly = assembly.CodeBase,
                    result = DoWork(42),
                    lateResult,
                    lateLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(item => item.GetName().Name == "Fx40.Environment.Late")
                });
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static int DoWork(int input)
        {
            int doubled = input * 2; // ENV_WEB_ENTRY
            int result = AddOne(doubled); // ENV_WEB_CALL
            GC.KeepAlive(input); // ENV_WEB_AFTER
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static int AddOne(int number)
        {
            int result = number + 1; // ENV_WEB_INNER
            return result;
        }
    }
}
