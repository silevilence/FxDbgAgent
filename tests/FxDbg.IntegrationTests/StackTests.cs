using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunStackInspection(string root, string configuration)
    {
        string architecture = IntPtr.Size == 4 ? "x86" : "x64";
        string name = "Fx40.Console." + architecture;
        string target = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
        string source = Path.Combine(root, "tests", "Debuggees", name, "Program.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index))
            .Single(item => item.text.Contains("breakpointValueSink = 1;")).index + 1;
        using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" --stack-threads",
            Path.GetDirectoryName(target)!, IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64,
            true, TimeSpan.FromSeconds(15));
        using Process process = Process.GetProcessById(session.Target.ProcessId);
        try
        {
            session.SetBreakpoint(new SourceLocation(source, line));
            session.Continue();
            ExpectError(FxDbgErrorCode.InvalidSessionState, () => session.GetStack(1));
            var stop = session.WaitForStop(TimeSpan.FromSeconds(15));
            var threads = session.GetThreads();
            Require(threads.Any(thread => thread.Name == "FxDbg-main") && threads.Any(thread => thread.Name == "FxDbg-worker"),
                "Named main and worker threads must be returned without executing user code.");
            Require(threads.All(thread => thread.ThreadId > 0 && thread.IsStopped && !string.IsNullOrEmpty(thread.AppDomain)),
                "Managed thread identity, AppDomain and stop state must be present.");
            var frames = session.GetStack(stop.ThreadId, 0, 32);
            StackFrameInfo[] targetFrames = frames.Where(frame => frame.ModuleName == target).ToArray();
            Require(targetFrames.Length == 14, "Expected 14 target frames; got " + targetFrames.Length);
            Require(targetFrames.First().MethodName.EndsWith(".BreakpointTarget") && targetFrames.Last().MethodName.EndsWith(".Main"),
                "Managed stack must run from BreakpointTarget to Main.");
            Require(targetFrames.All(frame => frame.SourceLocation is not null && frame.SourceLocation.Line > 0 &&
                frame.SourceLocation.FilePath == source && !string.IsNullOrEmpty(frame.AssemblyName) && !string.IsNullOrEmpty(frame.AppDomain)),
                "Each target frame must include assembly, module, source and AppDomain.");
            var page = session.GetStack(stop.ThreadId, 3, 4);
            Require(page.Count == 4 && page.Select(frame => frame.FrameId.Value).SequenceEqual(frames.Skip(3).Take(4).Select(frame => frame.FrameId.Value)),
                "Stack paging must preserve stable frame IDs within a stop.");
            ExpectError(FxDbgErrorCode.ThreadNotFound, () => session.GetStack(int.MaxValue));
            ExpectError(FxDbgErrorCode.InvalidRequest, () => session.GetStack(stop.ThreadId, -1));
            string output = Path.Combine(root, "artifacts", "stage1-6");
            Directory.CreateDirectory(output);
            File.WriteAllLines(Path.Combine(output, "stack-" + architecture + "-" + configuration.ToLowerInvariant() + ".txt"),
                targetFrames.Select(frame => frame.MethodName));
            Require(stop.BriefStack.Count == 5 && stop.BriefStack[0].FrameId.Equals(frames[0].FrameId),
                "Stop summaries must contain a bounded stack from the same stop generation.");
            session.Step(stop.ThreadId, StepKind.Over);
            session.WaitForStop(TimeSpan.FromSeconds(10));
            var nextFrames = session.GetStack(stop.ThreadId, 0, 4);
            Require(!nextFrames.Select(frame => frame.FrameId).Intersect(frames.Select(frame => frame.FrameId)).Any(),
                "Resuming must invalidate all prior frame IDs.");
            session.Terminate(TimeSpan.FromSeconds(5));
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
