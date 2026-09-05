using System;
using ClrDebug;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;

namespace FxDbg.Interop;

public sealed class FrameworkDebuggerBootstrap
{
    private const string RuntimeVersion = "v4.0.30319";

    public FrameworkDebugSession Launch(
        string commandLine,
        string workingDirectory,
        IntPtr environment,
        TargetArchitecture architecture,
        bool stopAtEntry,
        TimeSpan timeout,
        SessionId? sessionId = null)
    {
        RequireEngineArchitecture(architecture);
        return ManagedCreateProcessObserver.Start(
            timeout,
            architecture,
            true,
            stopAtEntry,
            corDebug => ClrDebug.Extensions.CreateProcess(
                corDebug,
                commandLine,
                dwCreationFlags: environment == IntPtr.Zero
                    ? (CreateProcessFlags)0
                    : CreateProcessFlags.CREATE_UNICODE_ENVIRONMENT,
                lpEnvironment: environment == IntPtr.Zero ? (IntPtr?)null : environment,
                lpCurrentDirectory: workingDirectory), sessionId);
    }

    public FrameworkDebugSession Attach(int processId, TargetArchitecture architecture, TimeSpan timeout, SessionId? sessionId = null)
    {
        RequireEngineArchitecture(architecture);
        return ManagedCreateProcessObserver.Start(
            timeout,
            architecture,
            false,
            false,
            corDebug => corDebug.DebugActiveProcess(processId, false), sessionId);
    }

    private static void RequireEngineArchitecture(TargetArchitecture requested)
    {
        TargetArchitecture actual = IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64;
        if (requested == TargetArchitecture.Auto)
        {
            throw new ArgumentException("Engine requests require a resolved architecture.", nameof(requested));
        }

        if (requested != actual)
        {
            throw new ArgumentException(
                $"Request architecture {requested.ToString().ToLowerInvariant()} does not match this Engine process.",
                nameof(requested));
        }
    }

    internal static CorDebug CreateDebugger()
    {
        var metaHost = new CLRMetaHost();
        CLRRuntimeInfo runtime = metaHost.GetRuntime(RuntimeVersion);
        return runtime.GetInterface().CorDebug;
    }
}
