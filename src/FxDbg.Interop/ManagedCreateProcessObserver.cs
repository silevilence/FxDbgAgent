using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Execution;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;

namespace FxDbg.Interop;

internal static class ManagedCreateProcessObserver
{
    internal static FrameworkDebugSession Start(
        TimeSpan timeout,
        TargetArchitecture architecture,
        bool launchedByDebugger,
        bool stopAtEntry,
        Func<CorDebug, CorDebugProcess> start,
        SessionId? sessionId, CancellationToken cancellationToken = default)
    {
        var callbacks = new BlockingCollection<CallbackEnvelope>();
        var callbackGate = new object();
        var callbackPairing = new ContinueStopCoordinator(TargetExecutionState.Running);
        long callbackSequence = 0;
        var callback = new CorDebugManagedCallback();
        callback.OnAnyEvent += (_, eventArgs) =>
        {
            lock (callbackGate)
            {
                callbacks.Add(new CallbackEnvelope(
                    Interlocked.Increment(ref callbackSequence),
                    eventArgs.Kind,
                    eventArgs.Controller,
                    eventArgs));
            }
        };

        CorDebug corDebug = FrameworkDebuggerBootstrap.CreateDebugger(out string runtimeVersion);
        CorDebugProcess? process = null;
        try
        {
            corDebug.Initialize();
            corDebug.SetManagedHandler(callback);
            cancellationToken.ThrowIfCancellationRequested();
            process = start(corDebug);
            string? runtimeFileVersion = null;
            CorDebugController? entryController = WaitForCreateProcess(
                callbacks,
                callbackPairing,
                timeout,
                stopAtEntry,
                () => runtimeFileVersion = GetRuntimeFileVersion(process.Id), cancellationToken);
            DebugSessionState state = stopAtEntry ? DebugSessionState.Stopped : DebugSessionState.Running;
            var target = new DebugTargetInfo(process.Id, architecture, runtimeVersion, launchedByDebugger, state, runtimeFileVersion);
            return new FrameworkDebugSession(
                target,
                corDebug,
                process,
                callback,
                callbacks,
                callbackGate,
                callbackPairing,
                entryController,
                sessionId ?? SessionId.New());
        }
        catch
        {
            if (process is not null)
            {
                BestEffortDetach(process);
            }

            try
            {
                corDebug.Terminate();
            }
            finally
            {
                callbacks.Dispose();
            }

            throw;
        }
    }

    private static CorDebugController? WaitForCreateProcess(
        BlockingCollection<CallbackEnvelope> callbacks,
        ContinueStopCoordinator callbackPairing,
        TimeSpan timeout,
        bool stopAtEntry,
        Action captureRuntimeVersion, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            int remaining = Math.Max(1, (int)Math.Min(int.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds));
            if (!callbacks.TryTake(out CallbackEnvelope envelope, remaining, cancellationToken))
            {
                throw new FxDbgException(
                    FxDbgErrorCode.OperationTimedOut,
                    $"No CreateProcess callback was received within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
            }

            if (envelope.Kind == CorDebugManagedCallbackKind.ExitProcess)
            {
                callbackPairing.MarkTerminated();
                throw new FxDbgException(FxDbgErrorCode.EngineExited, "Target exited before the CreateProcess callback was observed.");
            }

            callbackPairing.RecordCallbackStop(envelope.Sequence);
            if (envelope.Kind == CorDebugManagedCallbackKind.CreateProcess) captureRuntimeVersion();
            if (envelope.Kind == CorDebugManagedCallbackKind.CreateProcess && stopAtEntry)
            {
                return envelope.Controller;
            }

            callbackPairing.Continue(() => envelope.Controller.Continue(false));
            if (envelope.Kind == CorDebugManagedCallbackKind.CreateProcess)
            {
                return null;
            }
        }
    }

    private static string GetRuntimeFileVersion(int processId)
    {
        // Called on the command thread while CreateProcess still holds its native stop.
        using Process target = Process.GetProcessById(processId);
        ProcessModule runtime = target.Modules.Cast<ProcessModule>().Single(module =>
            string.Equals(module.ModuleName, "clr.dll", StringComparison.OrdinalIgnoreCase));
        return runtime.FileVersionInfo.FileVersion;
    }

    private static void BestEffortDetach(CorDebugProcess process)
    {
        try
        {
            process.Stop(0);
        }
        catch (Exception)
        {
        }

        try
        {
            process.Detach();
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class CallbackEnvelope
{
    internal CallbackEnvelope(long sequence, CorDebugManagedCallbackKind kind, CorDebugController controller,
        CorDebugManagedCallbackEventArgs eventArgs)
    {
        Sequence = sequence;
        Kind = kind;
        Controller = controller;
        EventArgs = eventArgs;
    }

    internal CorDebugManagedCallbackKind Kind { get; }

    internal CorDebugController Controller { get; }

    internal long Sequence { get; }

    internal CorDebugManagedCallbackEventArgs EventArgs { get; }
}
