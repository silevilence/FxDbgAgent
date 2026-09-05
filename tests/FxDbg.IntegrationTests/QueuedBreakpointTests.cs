using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunQueuedBreakpointRace(string root, string configuration)
    {
        string directory = Path.Combine(root, "artifacts", "queued-breakpoint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
        string source = Path.Combine(root, "tests", "Debuggees", "Fx40.ModuleLifecycle", "Program.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains("// RACE_BREAKPOINT")).index + 1;
        TargetArchitecture architecture = IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64;
        using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" --breakpoint-race \"" + directory + "\"",
            Path.GetDirectoryName(target)!, IntPtr.Zero, architecture, true, TimeSpan.FromSeconds(15));
        using Process process = Process.GetProcessById(session.Target.ProcessId);
        _ = process.Handle;
        try
        {
            var breakpoint = session.SetBreakpoint(new SourceLocation(source, line));
            session.Continue();
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "race-ready")));
            // Observe only the managed queue for a deterministic scheduling barrier; all native operations
            // still go through the real session API on its owner thread. No COM calls from the test.
            object queue = typeof(FrameworkDebugSession).GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            MethodInfo snapshot = queue.GetType().GetMethod("ToArray")!;
            bool BreakpointQueued()
            {
                object[] queued = ((Array)snapshot.Invoke(queue, null)!).Cast<object>().ToArray();
                if (queued.Any(item => item.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(item)!.ToString() == "Breakpoint")) return true;
                // A preceding module/thread callback can prevent the target from reaching the breakpoint.
                // Only consume when this snapshot guarantees a non-breakpoint is already at the head;
                // no other consumer can remove it on this session's owner thread.
                if (queued.Length > 0) session.PumpNextCallback(TimeSpan.Zero, CancellationToken.None);
                return false;
            }
            for (int index = 0; index < 32; index++)
            {
                File.WriteAllText(Path.Combine(directory, "go-" + index), "go");
                Require(SpinWait.SpinUntil(BreakpointQueued, TimeSpan.FromSeconds(10)), "The actual breakpoint callback must be queued before the command. Cycle=" + index +
                    "; breakpoint=" + Current(session, breakpoint).State + "; diagnostic=" + Current(session, breakpoint).Diagnostic +
                    "; native hit file=" + File.Exists(Path.Combine(directory, "hit-" + index)));
                Require(!session.IsStopped, "The queued callback must not have been consumed yet.");
                var pending = session.SetBreakpoint(new SourceLocation(Path.Combine(directory, "not-loaded.cs"), 1));
                session.SetBreakpointEnabled(pending.BreakpointId, false);
                session.RemoveBreakpoint(pending.BreakpointId);
                Require(!File.Exists(Path.Combine(directory, "hit-" + index)), "Temporary Stop/Continue must preserve the native breakpoint suspension.");
                PumpUntil(session, () => session.IsStopped);
                Require(session.HitBreakpointId == breakpoint.BreakpointId, "The queued hit identity must be preserved.");
                session.Continue();
                PumpUntil(session, () => File.Exists(Path.Combine(directory, "hit-" + index)));
            }
            session.RemoveBreakpoint(breakpoint.BreakpointId);
            session.Detach();
            Require(!process.HasExited, "Detach must preserve the controlled target.");
            File.WriteAllText(Path.Combine(directory, "race-done"), "done");
            Require(process.WaitForExit(5000) && process.ExitCode == 0, "The target must resume and exit cleanly.");
            Console.WriteLine("PASS: 32 queued-breakpoint Stop/Continue overlaps (" + (IntPtr.Size * 8) + " bit).");
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
