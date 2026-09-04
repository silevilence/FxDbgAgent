using System;
using System.Collections.Concurrent;
using System.Globalization;
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
        Func<CorDebug, CorDebugProcess> start)
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
                    eventArgs.Controller));
            }
        };

        CorDebug corDebug = FrameworkDebuggerBootstrap.CreateDebugger();
        CorDebugProcess? process = null;
        try
        {
            corDebug.Initialize();
            corDebug.SetManagedHandler(callback);
            process = start(corDebug);
            CorDebugController? entryController = WaitForCreateProcess(
                callbacks,
                callbackPairing,
                timeout,
                stopAtEntry);
            DebugSessionState state = stopAtEntry ? DebugSessionState.Stopped : DebugSessionState.Running;
            var target = new DebugTargetInfo(process.Id, architecture, "v4.0.30319", launchedByDebugger, state);
            return new FrameworkDebugSession(
                target,
                corDebug,
                process,
                callback,
                callbacks,
                callbackGate,
                callbackPairing,
                entryController);
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
        bool stopAtEntry)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            int remaining = Math.Max(1, (int)Math.Min(int.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds));
            if (!callbacks.TryTake(out CallbackEnvelope envelope, remaining))
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
    internal CallbackEnvelope(long sequence, CorDebugManagedCallbackKind kind, CorDebugController controller)
    {
        Sequence = sequence;
        Kind = kind;
        Controller = controller;
    }

    internal CorDebugManagedCallbackKind Kind { get; }

    internal CorDebugController Controller { get; }

    internal long Sequence { get; }
}
