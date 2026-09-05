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
    private static void RunVariableInspection(string root, string configuration)
    {
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
        string source = Path.Combine(root, "tests", "Debuggees", "Fx40.ModuleLifecycle", "Program.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains("// VARIABLE_BREAKPOINT")).index + 1;
        using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" --variables", Path.GetDirectoryName(target)!,
            IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64, true, TimeSpan.FromSeconds(15));
        using Process process = Process.GetProcessById(session.Target.ProcessId);
        _ = process.Handle;
        try
        {
            BreakpointInfo breakpoint = session.SetBreakpoint(new SourceLocation(source, line));
            session.Continue();
            var stop = session.WaitForStop(TimeSpan.FromSeconds(15));
            FrameId frame = session.GetStack(stop.ThreadId, 0, 1).Single().FrameId;
            var timer = Stopwatch.StartNew();
            var variables = session.GetVariables(frame, maxDepth: 0, maxStringLength: 8);
            VariableInfo Variable(string variableName) => variables.Single(item => item.Name == variableName);
            Require(Variable("number").DisplayValue == "42", "Integer argument must be read exactly.");
            Require(Variable("message").DisplayValue == "abcdefg…", "String argument must respect the requested length limit: " + Variable("message").DisplayValue + " " + Variable("message").Status + " " + Variable("message").Diagnostic);
            Require(Variable("nothing").Status == VariableStatus.Null || configuration == "Release" &&
                (Variable("nothing").Status == VariableStatus.OptimizedAway || Variable("nothing").Status == VariableStatus.Unavailable && Variable("nothing").Diagnostic!.Contains("CORDBG_E_READVIRTUAL_FAILURE")),
                "Null must be read or explicitly optimized away in Release: " + Variable("nothing").Status + " " + Variable("nothing").Diagnostic);
            Require(Variable("localNumber").Status == VariableStatus.Available ? Variable("localNumber").DisplayValue == "84"
                : configuration == "Release" && Variable("localNumber").Status == VariableStatus.OptimizedAway,
                "Local must be read correctly or explicitly reported as optimized away in Release.");
            Require(Variable("userCodeCalls").DisplayValue == "0", "Reading variables must not execute getters or ToString.");
            VariableInfo node = Variable("node");
            Require(node.ReferenceId is not null && node.Children.Count == 0, "Depth zero must preserve an expandable reference without eager traversal.");
            var children = session.GetVariables(frame, node.ReferenceId, count: 10, maxDepth: 4);
            Require(children.Single(item => item.Name == "Self").ReferenceId!.Equals(node.ReferenceId), "Cycle must retain the original reference ID.");
            Require(children.Single(item => item.Name == "Self").Children.Count == 0, "Cycle must not recursively expand.");
            Require(children.Single(item => item.Name == "Counter").DisplayValue == "777", "Static field must be read directly.");
            Require(children.Single(item => item.Name == "Empty").Status == VariableStatus.Null, "Readable null field must remain distinct from unavailable and optimized-away values.");
            Require(children.Single(item => item.Name == "Label").DisplayValue == "node-label", "Untruncated strings must retain their last character.");
            Require(children.All(item => item.Name != "Dangerous"), "Properties must not be evaluated as fields.");
            VariableInfo array = Variable("values");
            Require(array.TotalMembers == 100000, "Array length must be reported without walking all elements.");
            var page = session.GetVariables(frame, array.ReferenceId, start: 99999, count: 1, maxDepth: 0);
            Require(page.Count == 1 && page[0].Name == "[99999]" && page[0].DisplayValue == "909", "Array tail page must be read directly.");
            Require(session.GetVariables(frame, array.ReferenceId, start: 100000, count: 1).Count == 0, "Past-end array page must be empty.");
            ExpectError(FxDbgErrorCode.InvalidRequest, () => session.GetVariables(frame, maxDepth: 9));
            ExpectError(FxDbgErrorCode.InvalidRequest, () => session.GetVariables(frame, start: -1));
            Require(timer.Elapsed < TimeSpan.FromSeconds(5), "Bounded observations must not traverse the complete large array.");
            session.RemoveBreakpoint(breakpoint.BreakpointId);
            session.Step(stop.ThreadId, StepKind.Over);
            session.WaitForStop(TimeSpan.FromSeconds(10));
            FrameId nextFrame = session.GetStack(stop.ThreadId, 0, 1).Single().FrameId;
            ExpectError(FxDbgErrorCode.FrameNotFound, () => session.GetVariables(frame));
            ExpectError(FxDbgErrorCode.ValueUnavailable, () => session.GetVariables(nextFrame, node.ReferenceId));
            session.Continue();
            session.WaitForStop(TimeSpan.FromSeconds(10));
            Require(session.HasExited && process.WaitForExit(5000) && process.ExitCode == 0, "Target must confirm its formatting code was never invoked.");
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
