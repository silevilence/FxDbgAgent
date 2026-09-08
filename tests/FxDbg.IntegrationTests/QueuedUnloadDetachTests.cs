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
    private static void RunQueuedUnloadDetach(string root, string configuration)
    {
        foreach (string callbackKind in new[] { "UnloadModule", "ExitAppDomain" })
        {
            string directory = Path.Combine(root, "artifacts", "queued-unload-detach", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string library = Path.Combine(root, "tests/Debuggees/Fx40.LateModule/bin", configuration, "net40/Fx40.LateModule.dll");
            File.Copy(library, Path.Combine(directory, "Fx40.LateModule.dll"));
            File.Copy(Path.ChangeExtension(library, ".pdb"), Path.Combine(directory, "Fx40.LateModule.pdb"));
            string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
            string target = Path.Combine(root, "tests/Debuggees", name, "bin", configuration, "net40", name + ".exe");
            string source = Path.Combine(root, "tests/Debuggees/Fx40.LateModule/LateCode.cs");
            int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains("LATE_BREAKPOINT")).index + 1;
            using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" \"" + directory + "\"",
                Path.GetDirectoryName(target)!, IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64, true, TimeSpan.FromSeconds(15));
            using Process process = Process.GetProcessById(session.Target.ProcessId);
            _ = process.Handle;
            try
            {
                var breakpoint = session.SetBreakpoint(new SourceLocation(source, line));
                session.Continue();
                PumpUntil(session, () => File.Exists(Path.Combine(directory, "loaded-0")));
                File.WriteAllText(Path.Combine(directory, "go-0"), "go");
                PumpUntil(session, () => session.IsStopped);
                Require(session.HitBreakpointId == breakpoint.BreakpointId, "The retiring module must own a real active breakpoint.");
                string retiringId = session.GetModules().Single(module => module.Name == "Fx40.LateModule.dll").ModuleId;
                var tracked = (System.Collections.IEnumerable)typeof(FrameworkDebugSession).GetField("modules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                var retiring = tracked.Cast<object>().Select(pair => new
                {
                    Key = pair.GetType().GetProperty("Key")!.GetValue(pair)!,
                    Value = pair.GetType().GetProperty("Value")!.GetValue(pair)!
                }).Single(pair => (string)pair.Value.GetType().GetProperty("Id")!.GetValue(pair.Value)! == retiringId);
                object retiringRaw = retiring.Key;
                session.Continue();
                // Same managed-queue scheduling barrier as QueuedBreakpointTests: observe envelopes only,
                // without target reflection or native calls from the test. Leave the unload for Detach.
                object queue = typeof(FrameworkDebugSession).GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                MethodInfo snapshot = queue.GetType().GetMethod("ToArray")!;
                bool UnloadQueued()
                {
                    object[] queued = ((Array)snapshot.Invoke(queue, null)!).Cast<object>().ToArray();
                    bool Selected(object item)
                    {
                        if (item.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!.ToString() != callbackKind) return false;
                        if (callbackKind != "UnloadModule") return true;
                        object args = item.GetType().GetProperty("EventArgs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
                        object module = args.GetType().GetProperty("Module")!.GetValue(args)!;
                        return Equals(module.GetType().GetProperty("Raw")!.GetValue(module), retiringRaw);
                    }
                    if (queued.Length > 0 && Selected(queued[0])) return true;
                    if (queued.Length > 0) session.PumpNextCallback(TimeSpan.Zero, CancellationToken.None);
                    return false;
                }
                Require(SpinWait.SpinUntil(UnloadQueued, TimeSpan.FromSeconds(10)), callbackKind + " must be pending before detach.");
                session.Detach();
                Require(!process.HasExited, "Detach must leave the fixture alive at its next work gate.");
                File.WriteAllText(Path.Combine(directory, "go-1"), "go");
                File.WriteAllText(Path.Combine(directory, "go-2"), "go");
                Require(process.WaitForExit(5000) && process.ExitCode == 0 && File.Exists(Path.Combine(directory, "unloaded-2")),
                    "After detach the target must complete both remaining domains without an attached debugger.");
                Console.WriteLine("PASS: detach drains queued " + callbackKind + " and invalidates native bindings (" + (IntPtr.Size * 8) + " bit).");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }
        RunExceptionNoiseDetach(root, configuration);
    }

    private static void RunExceptionNoiseDetach(string root, string configuration)
    {
        string directory = Path.Combine(root, "artifacts", "queued-unload-detach", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests/Debuggees", name, "bin", configuration, "net40", name + ".exe");
        using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" --detach-noise \"" + directory + "\"",
            Path.GetDirectoryName(target)!, IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64, true, TimeSpan.FromSeconds(15));
        using Process process = Process.GetProcessById(session.Target.ProcessId);
        _ = process.Handle;
        try
        {
            session.ConfigureExceptionStops(true, ExceptionStopConfiguration.ParseRules("exact:System.ArgumentException"));
            session.Continue();
            PumpUntil(session, () => File.Exists(Path.Combine(directory, "noise-ready")));
            object queue = typeof(FrameworkDebugSession).GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            MethodInfo snapshot = queue.GetType().GetMethod("ToArray")!;
            bool ExceptionQueued()
            {
                object[] queued = ((Array)snapshot.Invoke(queue, null)!).Cast<object>().ToArray();
                if (queued.Any(item => item.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!.ToString() == "Exception2")) return true;
                if (queued.Length > 0) session.PumpNextCallback(TimeSpan.Zero, CancellationToken.None);
                return false;
            }
            Require(SpinWait.SpinUntil(ExceptionQueued, TimeSpan.FromSeconds(5)), "A real unmatched exception must be queued before detach.");
            session.Detach();
            Require(!process.HasExited, "The continuous exception producer must remain alive after detach.");
            File.WriteAllText(Path.Combine(directory, "noise-done"), "done");
            Require(process.WaitForExit(5000) && process.ExitCode == 0, "Ordinary callbacks must not require a quiet interval to detach.");
            Console.WriteLine("PASS: detach during continuous unmatched exceptions (" + (IntPtr.Size * 8) + " bit).");
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
