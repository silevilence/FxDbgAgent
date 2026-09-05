using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Interop;

namespace FxDbg.IntegrationTests;

internal static partial class Program
{
    private static void RunExceptionInspection(string root, string configuration, bool firstChance)
    {
        string name = IntPtr.Size == 4 ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle";
        string target = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
        string source = Path.Combine(root, "tests", "Debuggees", "Fx40.ModuleLifecycle", "Program.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains("// EXCEPTION_THROW")).index + 1;
        using var session = new FrameworkDebuggerBootstrap().Launch("\"" + target + "\" --exceptions", Path.GetDirectoryName(target)!,
            IntPtr.Zero, IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64, true, TimeSpan.FromSeconds(15));
        using Process process = Process.GetProcessById(session.Target.ProcessId);
        try
        {
            if (firstChance) session.ConfigureExceptionStops(true);
            session.Continue();
            StopInfo stop = session.WaitForStop(TimeSpan.FromSeconds(15));
            if (firstChance)
            {
                Require(stop.Reason == StopReason.Exception && stop.Exception?.Message == "handled-message" && !stop.Exception.IsUnhandled,
                    "Explicit first-chance policy must stop before the catch handler.");
                session.ConfigureExceptionStops(false);
                session.Continue();
                stop = session.WaitForStop(TimeSpan.FromSeconds(15));
            }
            Require(stop.Reason == StopReason.Exception && stop.Exception is not null, "Default policy must stop on the unhandled exception.");
            ExceptionInfo error = stop.Exception!;
            Require(error.IsUnhandled && error.TypeName.EndsWith("Program+ObservedException") && error.Message == "unhandled-message", "Exception must use its exact type and base storage message.");
            Require(error.ThreadId > 0 && error.ThreadId == stop.ThreadId, "Exception must identify its managed thread.");
            Require(error.ThrowLocation?.Line == line && string.Equals(error.ThrowLocation.FilePath, source, StringComparison.OrdinalIgnoreCase), "Exception must identify the original throw source.");
            Require(error.Stack.Count >= 2 && error.Stack[0].MethodName.EndsWith("ThrowObserved"), "Exception must include its managed throw stack.");
            FrameId frame = session.GetStack(stop.ThreadId, 0, 1).Single().FrameId;
            Require(session.GetVariables(frame, maxDepth: 0).Single(item => item.Name == "userCodeCalls").DisplayValue == "0", "Reporting the exception must not call custom Message or ToString.");
            session.Terminate(TimeSpan.FromSeconds(10));
            Require(session.HasExited && process.WaitForExit(5000), "Test target must be cleaned after inspecting the unhandled exception.");
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
