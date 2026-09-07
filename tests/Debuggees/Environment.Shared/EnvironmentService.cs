using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Threading;

namespace FxDbg.Debuggees.EnvironmentSetup
{
    // Environment smoke fixture, not a debugger or the complete stage 3-4 acceptance suite.
    internal sealed class EnvironmentService : ServiceBase
    {
        private readonly string heartbeatPath;
        private Timer timer;
        private int sequence;
        private int lateResult;
        private int writing;

        private EnvironmentService(string name, string path)
        {
            ServiceName = name;
            heartbeatPath = path;
            AutoLog = false;
        }

        private static void Main(string[] args)
        {
            if (args.Length != 2) throw new ArgumentException("Expected service name and heartbeat path.");
            Run(new EnvironmentService(args[0], args[1]));
        }

        protected override void OnStart(string[] args)
        {
            WriteHeartbeat(null);
            timer = new Timer(WriteHeartbeat, null, 1000, 1000);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteHeartbeat(object state)
        {
            if (Interlocked.Exchange(ref writing, 1) != 0) return;
            try
            {
            int current = Interlocked.Increment(ref sequence);
            int result = DoWork(current);
            string request = Path.Combine(Path.GetDirectoryName(heartbeatPath), "late.request");
            if (File.Exists(request))
            {
                File.Delete(request);
                Assembly late = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "late", "Fx40.Environment.Late.dll"));
                lateResult = (int)late.GetType("FxDbg.Debuggees.EnvironmentSetup.LateWork").GetMethod("Run").Invoke(null, new object[] { 42 });
            }
            using (Process process = Process.GetCurrentProcess())
            {
                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\"pid\":{0},\"bits\":{1},\"sessionId\":{2},\"sequence\":{3},\"result\":{4},\"utc\":\"{5:o}\",\"runtime\":\"{6}\",\"lateResult\":{7}}}",
                    process.Id, IntPtr.Size * 8, process.SessionId, current, result, DateTime.UtcNow, Environment.Version, lateResult);
                try { File.WriteAllText(heartbeatPath, json); }
                catch (IOException) { /* A concurrent health read must not stop the service. */ }
            }
            }
            finally { Interlocked.Exchange(ref writing, 0); }
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static int DoWork(int input)
        {
            int doubled = input * 2; // ENV_SERVICE_ENTRY
            int result = AddOne(doubled); // ENV_SERVICE_CALL
            GC.KeepAlive(input); // ENV_SERVICE_AFTER
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static int AddOne(int number)
        {
            int result = number + 1; // ENV_SERVICE_INNER
            return result;
        }

        protected override void OnStop()
        {
            Timer current = timer;
            if (current == null) return;
            using (var completed = new ManualResetEvent(false))
            {
                current.Dispose(completed);
                completed.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
    }
}
