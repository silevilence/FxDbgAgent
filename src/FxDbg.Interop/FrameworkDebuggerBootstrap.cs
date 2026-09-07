using System;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;

namespace FxDbg.Interop;

public sealed class FrameworkDebuggerBootstrap
{
    private const string RuntimeMoniker = "v4.0.30319";

    public FrameworkDebugSession Launch(
        string commandLine,
        string workingDirectory,
        IntPtr environment,
        TargetArchitecture architecture,
        bool stopAtEntry,
        TimeSpan timeout,
        SessionId? sessionId = null,
        CancellationToken cancellationToken = default)
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
                lpCurrentDirectory: workingDirectory), sessionId, cancellationToken);
    }

    public FrameworkDebugSession Attach(int processId, TargetArchitecture architecture, TimeSpan timeout, SessionId? sessionId = null, CancellationToken cancellationToken = default)
    {
        RequireEngineArchitecture(architecture);
        return ManagedCreateProcessObserver.Start(
            timeout,
            architecture,
            false,
            false,
            corDebug => AttachProcess(corDebug, processId), sessionId, cancellationToken);
    }

    private static CorDebugProcess AttachProcess(CorDebug debugger, int processId)
    {
        try { return debugger.DebugActiveProcess(processId, false); }
        catch (DebugException error) when (error.HResult == HRESULT.E_ACCESSDENIED)
        {
            throw new FxDbgException(FxDbgErrorCode.AccessDenied,
                "Engine CLR attach to target " + processId + " was denied. Start the Host with permission to debug this target and attach again.", error);
        }
        catch (DebugException error) when (error.HResult == HRESULT.CORDBG_E_DEBUGGER_ALREADY_ATTACHED)
        {
            throw new FxDbgException(FxDbgErrorCode.AlreadyDebugged,
                "Target process " + processId + " already has a debugger. Detach the existing debugger before attaching.", error);
        }
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
                $"Request architecture {TargetArchitectureWireName.Format(requested)} does not match this Engine process.",
                nameof(requested));
        }
    }

    internal static CorDebug CreateDebugger(out string runtimeVersion)
    {
        var metaHost = new CLRMetaHost();
        CLRRuntimeInfo runtime = metaHost.GetRuntime(RuntimeMoniker);
        runtimeVersion = runtime.VersionString;
        return runtime.GetInterface().CorDebug;
    }
}
