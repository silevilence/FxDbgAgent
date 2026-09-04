using System;
using System.Collections.Concurrent;
using System.Globalization;
using ClrDebug;
using FxDbg.Core.Errors;
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
        var callback = new CorDebugManagedCallback();
        callback.OnAnyEvent += (_, eventArgs) =>
        {
            lock (callbackGate)
            {
                callbacks.Add(new CallbackEnvelope(eventArgs.Kind, eventArgs.Controller));
            }
        };

        CorDebug corDebug = FrameworkDebuggerBootstrap.CreateDebugger();
        CorDebugProcess? process = null;
        try
        {
            corDebug.Initialize();
            corDebug.SetManagedHandler(callback);
            process = start(corDebug);
            CorDebugController? entryController = WaitForCreateProcess(callbacks, timeout, stopAtEntry);
            DebugSessionState state = stopAtEntry ? DebugSessionState.Stopped : DebugSessionState.Running;
            var target = new DebugTargetInfo(process.Id, architecture, "v4.0.30319", launchedByDebugger, state);
            return new FrameworkDebugSession(target, corDebug, process, callback, callbacks, callbackGate, entryController);
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
                throw new FxDbgException(FxDbgErrorCode.EngineExited, "Target exited before the CreateProcess callback was observed.");
            }

            if (envelope.Kind == CorDebugManagedCallbackKind.CreateProcess && stopAtEntry)
            {
                return envelope.Controller;
            }

            envelope.Controller.Continue(false);
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
    internal CallbackEnvelope(CorDebugManagedCallbackKind kind, CorDebugController controller)
    {
        Kind = kind;
        Controller = controller;
    }

    internal CorDebugManagedCallbackKind Kind { get; }

    internal CorDebugController Controller { get; }
}
