using System;
using System.Collections.Concurrent;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;

namespace FxDbg.Interop;

public sealed class FrameworkDebugSession : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(4);

    private readonly CorDebug corDebug;
    private readonly CorDebugProcess process;
    private readonly CorDebugManagedCallback callback;
    private readonly BlockingCollection<CallbackEnvelope> callbacks;
    private readonly object callbackGate;
    private CorDebugController? pendingEntryController;
    private bool processExited;
    private bool disposed;

    internal FrameworkDebugSession(
        DebugTargetInfo target,
        CorDebug corDebug,
        CorDebugProcess process,
        CorDebugManagedCallback callback,
        BlockingCollection<CallbackEnvelope> callbacks,
        object callbackGate,
        CorDebugController? pendingEntryController)
    {
        Target = target;
        this.corDebug = corDebug;
        this.process = process;
        this.callback = callback;
        this.callbacks = callbacks;
        this.callbackGate = callbackGate;
        this.pendingEntryController = pendingEntryController;
    }

    public DebugTargetInfo Target { get; }

    public void PumpCallbacksUntil(Func<bool> shouldStop)
    {
        if (shouldStop is null)
        {
            throw new ArgumentNullException(nameof(shouldStop));
        }

        ThrowIfDisposed();
        while (!shouldStop())
        {
            if (callbacks.TryTake(out CallbackEnvelope envelope, 100))
            {
                HandleCallback(envelope, false);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        bool debuggerCanTerminate = false;
        try
        {
            ReleaseEntryStop();
            SynchronizeAndDetach();
            debuggerCanTerminate = true;
            WaitForCallbackProducerBarrier();
            DrainDetachedCallbacks();
        }
        finally
        {
            GC.KeepAlive(callback);
            if (debuggerCanTerminate)
            {
                try
                {
                    corDebug.Terminate();
                }
                finally
                {
                    callbacks.Dispose();
                }
            }
        }
    }

    private void ReleaseEntryStop()
    {
        CorDebugController? entryController = pendingEntryController;
        pendingEntryController = null;
        if (entryController is null)
        {
            return;
        }

        try
        {
            entryController.Continue(false);
        }
        catch (Exception)
        {
            // Synchronization below still attempts a safe Detach or waits for ExitProcess.
        }
    }

    private void SynchronizeAndDetach()
    {
        DateTime deadline = DateTime.UtcNow.Add(ShutdownTimeout);
        bool manualStopOutstanding = false;
        bool awaitingQueuedCallback = false;
        Exception? lastFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            WaitForCallbackProducerBarrier();
            if (callbacks.TryTake(out CallbackEnvelope envelope))
            {
                awaitingQueuedCallback = false;
                HandleCallback(envelope, true);
                if (processExited)
                {
                    return;
                }

                continue;
            }

            if (awaitingQueuedCallback)
            {
                if (callbacks.TryTake(out envelope, 25))
                {
                    awaitingQueuedCallback = false;
                    HandleCallback(envelope, true);
                    if (processExited)
                    {
                        return;
                    }
                }

                continue;
            }

            if (HasTargetExited())
            {
                WaitForExitProcess(deadline);
                return;
            }

            if (!manualStopOutstanding)
            {
                try
                {
                    process.Stop(0);
                    manualStopOutstanding = true;
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                    if (HasTargetExited())
                    {
                        WaitForExitProcess(deadline);
                        return;
                    }

                    Thread.Sleep(10);
                }

                continue;
            }

            try
            {
                if (process.HasQueuedCallbacks(null!))
                {
                    process.Continue(false);
                    manualStopOutstanding = false;
                    awaitingQueuedCallback = true;
                    continue;
                }

                process.Detach();
                return;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                if (HasTargetExited())
                {
                    WaitForExitProcess(deadline);
                    return;
                }

                Thread.Sleep(10);
            }
        }

        throw new FxDbgException(
            FxDbgErrorCode.OperationTimedOut,
            $"The Engine could not synchronize and detach from the target within {ShutdownTimeout.TotalSeconds} seconds.",
            lastFailure ?? new InvalidOperationException("Detach did not complete."));
    }

    private void WaitForExitProcess(DateTime deadline)
    {
        while (!processExited && DateTime.UtcNow < deadline)
        {
            WaitForCallbackProducerBarrier();
            if (callbacks.TryTake(out CallbackEnvelope envelope, 25))
            {
                HandleCallback(envelope, true);
            }
        }

        if (!processExited)
        {
            throw new FxDbgException(
                FxDbgErrorCode.OperationTimedOut,
                "The target exited without delivering the required ExitProcess callback.");
        }
    }

    private void WaitForCallbackProducerBarrier()
    {
        lock (callbackGate)
        {
        }
    }

    private void HandleCallback(CallbackEnvelope envelope, bool suppressContinueFailure)
    {
        if (envelope.Kind == CorDebugManagedCallbackKind.ExitProcess)
        {
            processExited = true;
            return;
        }

        if (!suppressContinueFailure)
        {
            envelope.Controller.Continue(false);
            return;
        }

        try
        {
            envelope.Controller.Continue(false);
        }
        catch (Exception)
        {
            // The shutdown loop will retry synchronization or wait for ExitProcess.
        }
    }

    private bool HasTargetExited()
    {
        try
        {
            using System.Diagnostics.Process target = System.Diagnostics.Process.GetProcessById(Target.ProcessId);
            return target.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private void DrainDetachedCallbacks()
    {
        while (callbacks.TryTake(out CallbackEnvelope envelope))
        {
            if (envelope.Kind == CorDebugManagedCallbackKind.ExitProcess)
            {
                processExited = true;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(FrameworkDebugSession));
        }
    }
}
