using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunExecutionControl(string root, string configuration)
    {
        string directory = Path.Combine(root, "artifacts", "stage1-5", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string targetName = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests", "Debuggees", targetName, "bin", configuration, "net40", targetName + ".exe");
        string source = Path.Combine(root, "tests", "Debuggees", "Fx40.ModuleLifecycle", "Program.cs");
        int Line(string marker) => File.ReadAllLines(source).Select((text, index) => (text, index))
            .Single(item => item.text.TrimEnd().EndsWith("// " + marker, StringComparison.Ordinal)).index + 1;
        TargetArchitecture architecture = IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64;
        var bootstrap = new FrameworkDebuggerBootstrap();
        string command = "\"" + target + "\" --execution \"" + directory + "\"";
        using (var session = bootstrap.Launch(command, Path.GetDirectoryName(target)!, IntPtr.Zero, architecture, true, TimeSpan.FromSeconds(15)))
        using (Process process = Process.GetProcessById(session.Target.ProcessId))
        {
            try
            {
                Require(session.State == DebugSessionState.Stopped && session.CurrentStop!.Reason == StopReason.Entry,
                    "Entry stop must agree with the session state.");
                BreakpointInfo breakpoint = session.SetBreakpoint(new SourceLocation(source, Line("STEP_CALL")));
                session.Continue();
                ExpectError(FxDbgErrorCode.InvalidSessionState, session.Continue);
                StopInfo stop = session.WaitForStop(TimeSpan.FromSeconds(15));
                Require(stop.Location?.Line == Line("STEP_CALL"), "Breakpoint stop must map to the call line.");
                int threadId = stop.ThreadId;
                session.RemoveBreakpoint(breakpoint.BreakpointId);
                ExpectError(FxDbgErrorCode.InvalidRequest, () => session.Step(0, StepKind.Into));
                ExpectError(FxDbgErrorCode.ThreadNotFound, () => session.Step(int.MaxValue, StepKind.Into));
                session.Step(threadId, StepKind.Into);
                stop = session.WaitForStop(TimeSpan.FromSeconds(10));
                Require(stop.Reason == StopReason.Step && stop.MethodName!.EndsWith(".AddOne"), "Step Into must enter AddOne.");
                Require(stop.Location?.Line == Line("STEP_INNER_ENTRY") || stop.Location?.Line == Line("STEP_INNER"),
                    "Step Into must report the method's executable source entry.");
                session.Step(threadId, StepKind.Out);
                stop = session.WaitForStop(TimeSpan.FromSeconds(10));
                Require(stop.MethodName!.EndsWith(".RunExecution"), "Step Out must return to its caller.");
                if (stop.Location?.Line == Line("STEP_CALL"))
                {
                    session.Step(threadId, StepKind.Over);
                    stop = session.WaitForStop(TimeSpan.FromSeconds(10));
                }
                Require(stop.Location?.Line == Line("STEP_AFTER"), "Step Out must return at the caller source statement.");
                session.Step(threadId, StepKind.Over);
                stop = session.WaitForStop(TimeSpan.FromSeconds(10));
                Require(stop.Location?.Line == Line("STEP_OVER"), "Step Over must advance one source statement.");
                session.Step(threadId, StepKind.Over);
                stop = session.WaitForStop(TimeSpan.FromSeconds(10));
                Require(stop.Location?.Line == Line("STEP_READY"), "Step Over must skip the AddOne call.");
                ExpectError(FxDbgErrorCode.InvalidSessionState, () => session.Pause());
                session.Continue();
                PumpUntil(session, () => File.Exists(Path.Combine(directory, "execution-ready")));
                ExpectError(FxDbgErrorCode.OperationTimedOut, () => session.WaitForStop(TimeSpan.FromMilliseconds(100)));
                Require(session.State == DebugSessionState.Stopped, "Timed-out wait must leave an inspectable stop.");
                session.Continue();
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    ExpectError(FxDbgErrorCode.OperationCancelled, () => session.WaitForStop(TimeSpan.FromSeconds(10), cancellation.Token));
                }
                Require(session.IsStopped, "Cancelled execution wait must pause the target.");
                session.SetBreakpoint(new SourceLocation(source, Line("STEP_CALL")));
                session.Detach();
                Require(session.State == DebugSessionState.Terminated && !process.HasExited, "Launch-mode Detach must leave target alive.");
                Require(session.Events.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, session.Events.Count).Select(value => (long)value)),
                    "Session events must have contiguous sequence numbers.");
                File.WriteAllText(Path.Combine(directory, "execution-done"), "done");
                Require(process.WaitForExit(5000), "Detached target must continue to completion.");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }

        using (var session = bootstrap.Launch(command, Path.GetDirectoryName(target)!, IntPtr.Zero, architecture, true, TimeSpan.FromSeconds(15)))
        using (Process process = Process.GetProcessById(session.Target.ProcessId))
        {
            try
            {
                session.Terminate(TimeSpan.FromSeconds(5));
                Require(session.State == DebugSessionState.Terminated && session.HasExited, "Terminate must observe actual target exit.");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }

        string terminateDirectory = Path.Combine(directory, "terminate-running");
        Directory.CreateDirectory(terminateDirectory);
        using (var session = bootstrap.Launch("\"" + target + "\" --execution \"" + terminateDirectory + "\"",
            Path.GetDirectoryName(target)!, IntPtr.Zero, architecture, false, TimeSpan.FromSeconds(15)))
        using (Process process = Process.GetProcessById(session.Target.ProcessId))
        {
            try
            {
                PumpUntil(session, () => File.Exists(Path.Combine(terminateDirectory, "execution-ready")));
                session.Terminate(TimeSpan.FromSeconds(5));
                Require(session.HasExited && session.State == DebugSessionState.Terminated, "Running-mode Terminate must observe actual target exit.");
                ExpectError(FxDbgErrorCode.InvalidSessionState, session.Continue);
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }

        string attachDirectory = Path.Combine(directory, "attach");
        Directory.CreateDirectory(attachDirectory);
        using Process attachedTarget = Process.Start(new ProcessStartInfo(target, "--execution \"" + attachDirectory + "\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(attachDirectory, "execution-ready")))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Attach target not ready.");
                Thread.Sleep(10);
            }
            using var session = bootstrap.Attach(attachedTarget.Id, architecture, TimeSpan.FromSeconds(15));
            // Drain initial attach callbacks before exercising a public Pause.
            for (int index = 0; index < 100; index++) session.PumpNextCallback(TimeSpan.FromMilliseconds(5), CancellationToken.None);
            session.Pause();
            ExpectError(FxDbgErrorCode.InvalidRequest, () => session.Terminate(TimeSpan.FromSeconds(5)));
            session.Detach();
            Require(!attachedTarget.HasExited, "Attach-mode Detach and rejected Terminate must preserve the target.");
            File.WriteAllText(Path.Combine(attachDirectory, "execution-done"), "done");
            Require(attachedTarget.WaitForExit(5000), "Detached attached target must continue to completion.");
        }
        finally { if (!attachedTarget.HasExited) { attachedTarget.Kill(); attachedTarget.WaitForExit(5000); } }
    }

    private static void ExpectError(FxDbgErrorCode expected, Action action)
    {
        try { action(); }
        catch (FxDbgException exception)
        {
            Require(exception.Code == expected, "Expected " + expected + "; got " + exception.Code + ": " + exception.Message);
            return;
        }
        throw new InvalidOperationException("Expected failure " + expected + ".");
    }
}
