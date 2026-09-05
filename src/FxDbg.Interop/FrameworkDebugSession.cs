using System;
using System.Collections.Concurrent;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Execution;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(4);

    private readonly CorDebug corDebug;
    private readonly CorDebugProcess process;
    private readonly CorDebugManagedCallback callback;
    private readonly BlockingCollection<CallbackEnvelope> callbacks;
    private readonly object callbackGate;
    private readonly int owningThreadId;
    private readonly ContinueStopCoordinator callbackPairing;
    private CorDebugController? pendingEntryController;
    private CallbackEnvelope? pendingCallbackContinue;
    private bool processExited;
    private bool disposed;

    internal FrameworkDebugSession(
        DebugTargetInfo target,
        CorDebug corDebug,
        CorDebugProcess process,
        CorDebugManagedCallback callback,
        BlockingCollection<CallbackEnvelope> callbacks,
        object callbackGate,
        ContinueStopCoordinator callbackPairing,
        CorDebugController? pendingEntryController,
        SessionId sessionId)
    {
        initialTarget = target;
        domain = new DebugSession(sessionId);
        domain.Start();
        this.corDebug = corDebug;
        this.process = process;
        this.callback = callback;
        this.callbacks = callbacks;
        this.callbackGate = callbackGate;
        this.callbackPairing = callbackPairing;
        owningThreadId = Environment.CurrentManagedThreadId;
        this.pendingEntryController = pendingEntryController;
        if (pendingEntryController is not null)
        {
            CurrentStop = new StopInfo(StopReason.Entry, target.ProcessId, 0, null, null);
            domain.MarkStopped(CurrentStop);
        }
        else domain.MarkRunning("process_started");
    }

    private readonly DebugTargetInfo initialTarget;
    private readonly DebugSession domain;

    public DebugTargetInfo Target => new(initialTarget.ProcessId, initialTarget.Architecture,
        initialTarget.RuntimeVersion, initialTarget.LaunchedByDebugger, domain.State);

    public bool PumpNextCallback(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ThrowIfDisposed();
        ThrowIfWrongThread();
        int milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? Timeout.Infinite
            : (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalMilliseconds));
        PollSymbolFiles();
        if (pendingEntryController is not null) return false;
        if (callbacks.TryTake(out CallbackEnvelope envelope, milliseconds, cancellationToken))
        {
            HandleCallback(envelope, false);
            return true;
        }

        return false;
    }

    public ContinuePairingSnapshot RunPauseContinueCycles(
        int cycleCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (cycleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleCount));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ThrowIfDisposed();
        ThrowIfWrongThread();
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        DrainCallbacksUntilQuiet(deadline, cancellationToken);
        long initialStops = callbackPairing.StopCount;
        long initialContinues = callbackPairing.ContinueCount;
        for (int index = 0; index < cycleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new FxDbgException(
                    FxDbgErrorCode.OperationTimedOut,
                    $"Pause/Continue verification did not finish within {timeout.TotalSeconds} seconds.");
            }

            process.Stop(0);
            callbackPairing.RecordManualStop();
            callbackPairing.Continue(() => process.Continue(false));
        }

        return new ContinuePairingSnapshot(
            callbackPairing.StopCount - initialStops,
            callbackPairing.ContinueCount - initialContinues,
            callbackPairing.OutstandingStopCount);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        ThrowIfWrongThread();
        disposed = true;
        if (domain.State != DebugSessionState.Terminated && domain.State != DebugSessionState.Failed) domain.BeginDetach();
        bool debuggerCanTerminate = false;
        try
        {
            ReleaseEntryStop();
            SynchronizeAndDetach();
            debuggerCanTerminate = true;
            WaitForCallbackProducerBarrier();
            DrainDetachedCallbacks();
            if (domain.State != DebugSessionState.Terminated && domain.State != DebugSessionState.Failed) domain.MarkTerminated("detached");
        }
        catch
        {
            if (domain.State != DebugSessionState.Terminated && domain.State != DebugSessionState.Failed) domain.Fail("detach_failed");
            throw;
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
                    ClearModules();
                }
            }
        }
    }

    private void ReleaseEntryStop()
    {
        CorDebugController? entryController = pendingEntryController;
        if (entryController is null)
        {
            return;
        }

        try
        {
            callbackPairing.Continue(() => entryController.Continue(false));
            pendingEntryController = null;
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
            if (pendingEntryController is not null)
            {
                try
                {
                    callbackPairing.Continue(() => pendingEntryController.Continue(false));
                    pendingEntryController = null;
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
                    continue;
                }
            }

            if (pendingCallbackContinue is not null)
            {
                try
                {
                    CallbackEnvelope pending = pendingCallbackContinue;
                    callbackPairing.Continue(() => pending.Controller.Continue(false));
                    pendingCallbackContinue = null;
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
                    continue;
                }
            }

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

                stepper?.Deactivate();
                stepper = null;
                breakpoints.Dispose();
                process.Detach();
                ClearModules();
                callbackPairing.MarkTerminated();
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
            pendingEntryController = null;
            callbackPairing.MarkTerminated();
            CurrentStop = new StopInfo(StopReason.ProcessExit, Target.ProcessId, 0, null, null);
            if (domain.State != DebugSessionState.Terminated && domain.State != DebugSessionState.Failed) domain.MarkTerminated("process_exited");
            ClearModules();
            return;
        }

        callbackPairing.RecordCallbackStop(envelope.Sequence);
        if (!suppressContinueFailure)
        {
            try
            {
                if (HandleBreakpointCallback(envelope)) return;
            }
            catch
            {
                // Preserve the single outstanding stop when command-thread processing fails.
                pendingEntryController = envelope.Controller;
                throw;
            }
        }
        if (!suppressContinueFailure)
        {
            callbackPairing.Continue(() => envelope.Controller.Continue(false));
            return;
        }

        try
        {
            callbackPairing.Continue(() => envelope.Controller.Continue(false));
        }
        catch (Exception)
        {
            pendingCallbackContinue = envelope;
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
                callbackPairing.MarkTerminated();
            }
        }
    }

    private void DrainCallbacksUntilQuiet(DateTime deadline, CancellationToken cancellationToken)
    {
        DateTime quietDeadline = DateTime.UtcNow.AddMilliseconds(100);
        while (DateTime.UtcNow < quietDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new FxDbgException(
                    FxDbgErrorCode.OperationTimedOut,
                    "Callback queue did not become quiet before the operation timed out.");
            }

            if (callbacks.TryTake(out CallbackEnvelope envelope, 10, cancellationToken))
            {
                HandleCallback(envelope, false);
                quietDeadline = DateTime.UtcNow.AddMilliseconds(100);
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

    private void ThrowIfWrongThread()
    {
        if (Environment.CurrentManagedThreadId != owningThreadId)
        {
            throw new FxDbgException(
                FxDbgErrorCode.InternalError,
                "ICorDebug operations must run on the Engine command scheduler thread.");
        }
    }
}

public sealed class ContinuePairingSnapshot
{
    internal ContinuePairingSnapshot(long stopCount, long continueCount, long outstandingStopCount)
    {
        StopCount = stopCount;
        ContinueCount = continueCount;
        OutstandingStopCount = outstandingStopCount;
    }

    public long StopCount { get; }

    public long ContinueCount { get; }

    public long OutstandingStopCount { get; }
}
