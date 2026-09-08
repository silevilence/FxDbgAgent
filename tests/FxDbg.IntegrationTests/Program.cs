using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Events;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 2 && args[2] == "exception-filters") { RunExceptionFilters(Path.GetFullPath(args[0]),args[1]); return 0; }
            if (args.Length > 2 && args[2] == "evaluation") { RunEvaluation(Path.GetFullPath(args[0]),args[1]); return 0; }
            if (args.Length > 2 && args[2] == "breakpoint-race")
            {
                RunQueuedBreakpointRace(Path.GetFullPath(args[0]), args[1]);
                return 0;
            }
            if (args.Length > 2 && args[2] == "modules")
            {
                RunModuleInspection(args[0], args[1]);
                Console.WriteLine("PASS: module identities, symbol states, reload and cache cleanup (" + (IntPtr.Size * 8) + " bit).");
                return 0;
            }
            if (args.Length > 2 && args[2] == "exceptions")
            {
                RunExceptionInspection(args[0], args[1], false);
                RunExceptionInspection(args[0], args[1], true);
                Console.WriteLine("PASS: unhandled exception, first-chance policy and safe raw message (" + (IntPtr.Size * 8) + " bit).");
                return 0;
            }
            if (args.Length > 2 && args[2] == "variables")
            {
                RunVariableInspection(Path.GetFullPath(args[0]), args[1]);
                Console.WriteLine("PASS: arguments, locals, fields, bounded strings, array pages and cycle references (" + (IntPtr.Size * 8) + " bit).");
                return 0;
            }
            if (args.Length > 2 && args[2] == "stack")
            {
                RunStackInspection(Path.GetFullPath(args[0]), args[1]);
                Console.WriteLine("PASS: named managed threads and paged 14-frame source stack (" + (IntPtr.Size * 8) + " bit).");
                return 0;
            }
            if (args.Length > 2 && args[2] == "execution")
            {
                RunExecutionControl(Path.GetFullPath(args[0]), args[1]);
                Console.WriteLine("PASS: execution control, source stepping, timeout/cancellation, detach and terminate (" + (IntPtr.Size * 8) + " bit).");
                return 0;
            }
            RunBreakpointLifecycle(Path.GetFullPath(args[0]), args.Length > 1 ? args[1] : "Debug");
            Console.WriteLine("PASS: real breakpoint lifecycle, delayed PDB, enable/disable/remove, AppDomain unload/reload (" + (IntPtr.Size * 8) + " bit).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunBreakpointLifecycle(string root, string configuration)
    {
        string directory = Path.Combine(root, "artifacts", "stage1-4", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string library = Path.Combine(root, "tests", "Debuggees", "Fx40.LateModule", "bin", configuration, "net40", "Fx40.LateModule.dll");
        File.Copy(library, Path.Combine(directory, "Fx40.LateModule.dll"));
        string targetName = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests", "Debuggees", targetName, "bin", configuration, "net40", targetName + ".exe");
        string source = Path.Combine(root, "tests", "Debuggees", "Fx40.LateModule", "LateCode.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains("LATE_BREAKPOINT")).index + 1;
        var changes = new List<BreakpointChange>();
        int processId = 0;
        try
        {
            using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" \"" + directory + "\"",
                Path.GetDirectoryName(target)!, IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64,
                true, TimeSpan.FromSeconds(15));
            processId = session.Target.ProcessId;
            session.BreakpointChanged += changes.Add;
            BreakpointInfo breakpoint = session.SetBreakpoint(new SourceLocation(source, line));
            Require(breakpoint.State == BreakpointState.Pending, "Breakpoint must initially be pending.");
            session.SetBreakpointEnabled(breakpoint.BreakpointId, false);
            session.Continue();
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-0")));
            Require(Current(session, breakpoint).State == BreakpointState.Pending, "Missing PDB must preserve pending source breakpoint.");
            File.Copy(Path.ChangeExtension(library, ".pdb"), Path.Combine(directory, "Fx40.LateModule.pdb"));
            PumpUntil(session, () => Current(session, breakpoint).State == BreakpointState.Verified);
            Require(!Current(session, breakpoint).Enabled, "PDB readiness must preserve disabled state.");
            File.WriteAllText(Path.Combine(directory, "go-0"), "go");
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-1")));
            Require(!session.IsStopped, "Disabled breakpoint must not stop execution.");
            session.SetBreakpointEnabled(breakpoint.BreakpointId, true);
            File.WriteAllText(Path.Combine(directory, "go-1"), "go");
            PumpUntil(session, () => session.IsStopped);
            Require(session.HitBreakpointId == breakpoint.BreakpointId, "Reloaded source breakpoint must be identified at the actual hit.");
            Require(session.StoppedThreadId > 0, "Breakpoint hit must identify the target thread.");
            session.RemoveBreakpoint(breakpoint.BreakpointId);
            session.Continue();
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-2")));
            File.WriteAllText(Path.Combine(directory, "go-2"), "go");
            PumpUntil(session, () => session.HasExited);
            Require(session.GetBreakpoints().Count == 0, "Removed breakpoint must not rebind.");
            BreakpointState[] states = changes.Where(change => !change.Removed).Select(change => change.Breakpoint.State).ToArray();
            int verified = Array.IndexOf(states, BreakpointState.Verified);
            Require(verified >= 0 && states.Skip(verified + 1).Contains(BreakpointState.Pending), "Unload must produce a pending event.");
            Require(states.Count(state => state == BreakpointState.Verified) >= 2, "Reload must produce a verified event.");
            Require(changes.Count(change => change.Removed) == 1, "Removal must produce exactly one event.");
            var domainEvents = session.Events.OfType<AppDomainChangedEvent>().ToArray();
            var unloaded = domainEvents.Where(item => item.Change == AppDomainChangeKind.Exited).ToArray();
            Require(unloaded.Length >= 3 && unloaded.Select(item => item.AppDomain.AppDomainId).Distinct().Count() == unloaded.Length,
                "Each unloaded AppDomain has a distinct lifetime identity.");
            Require(unloaded.All(item => domainEvents.Any(created => created.Change == AppDomainChangeKind.Created && created.AppDomain.AppDomainId == item.AppDomain.AppDomainId)),
                "Each domain exit corresponds to its observed creation.");
            Require(session.Events.OfType<ModuleChangedEvent>().All(item => item.Module.AppDomainId is not null), "Module events carry domain identities.");
        }
        finally
        {
            if (processId != 0)
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
                }
                catch (ArgumentException) { }
            }
        }
    }

    private static BreakpointInfo Current(FrameworkDebugSession session, BreakpointInfo breakpoint) =>
        session.GetBreakpoints().Single(item => item.BreakpointId == breakpoint.BreakpointId);

    private static void PumpUntil(FrameworkDebugSession session, Func<bool> complete)
    {
        var timer = Stopwatch.StartNew();
        while (!complete())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Integration test condition did not become true.");
            session.PumpNextCallback(TimeSpan.FromMilliseconds(10), CancellationToken.None);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
