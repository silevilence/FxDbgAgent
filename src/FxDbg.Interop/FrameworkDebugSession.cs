using System;
using System.Collections.Concurrent;
using System.IO;
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
    private readonly TargetProcessLifetime targetLifetime;
    private CorDebugController? pendingEntryController;
    private CallbackEnvelope? pendingCallbackContinue;
    private string? startupModulePath;
    private bool startupModuleContinuePending;
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
            if (target.LaunchedByDebugger) startupModulePath = TargetProcessLifetime.ReadImagePath(process.Handle);
            CurrentStop = new StopInfo(StopReason.Entry, target.ProcessId, 0, null, null);
            domain.MarkStopped(CurrentStop);
        }
        else domain.MarkRunning("process_started");
        targetLifetime = new TargetProcessLifetime(process.Handle);
    }

    private readonly DebugTargetInfo initialTarget;
    private readonly DebugSession domain;

    public DebugTargetInfo Target => new(initialTarget.ProcessId, initialTarget.Architecture,
        initialTarget.RuntimeVersion, initialTarget.LaunchedByDebugger, domain.State, initialTarget.RuntimeFileVersion);

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
            targetLifetime.Dispose();
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
            ContinueCallback(() => entryController.Continue(false));
            pendingEntryController = null;
        }
        catch (Exception)
        {
            // Synchronization below still attempts a safe Detach or waits for ExitProcess.
        }
    }

    private void SynchronizeAndDetach()
    {
        if (processExited) return;
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
                    ContinueCallback(() => pendingEntryController.Continue(false));
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
                    ContinueCallback(() => pending.Controller.Continue(false));
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

            // CreateProcess is earlier than the CLR startup module handshake. Let that
            // callback complete before introducing a manual Stop followed by Detach.
            if (awaitingQueuedCallback || startupModulePath is not null || unloadingBreakpointDomains.Count != 0 || DateTime.UtcNow < unloadQuietDeadline)
            {
                if (manualStopOutstanding)
                {
                    // A callback may have arrived after Stop added its independent native count.
                    // The CLR cannot finish unloading until that manual count is also released.
                    process.Continue(false);
                    manualStopOutstanding = false;
                }
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

                if (exception is DebugException native && native.HResult == HRESULT.CORDBG_E_DETACH_FAILED_OUTSTANDING_BREAKPOINTS)
                {
                    // A module-unload callback can precede CLR removal of its native breakpoints.
                    // Release only our manual Stop so that unload can finish before resynchronizing.
                    process.Continue(false);
                    manualStopOutstanding = false;
                }
                Thread.Sleep(10);
            }
        }

        throw new FxDbgException(
            FxDbgErrorCode.OperationTimedOut,
            $"The Engine could not synchronize and detach from the target within {ShutdownTimeout.TotalSeconds} seconds.",
            lastFailure ?? new InvalidOperationException("Detach did not complete."));
    }

    private void WaitForExitProcess(DateTime deadline, bool terminationAccepted = false)
    {
        while (!processExited && DateTime.UtcNow < deadline)
        {
            WaitForCallbackProducerBarrier();
            if (callbacks.TryTake(out CallbackEnvelope envelope, 25))
            {
                // Terminate invalidates native stops. A callback already queued before it
                // succeeded must not be counted or continued against the terminated process.
                if (terminationAccepted && envelope.Kind != CorDebugManagedCallbackKind.ExitProcess) continue;
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
            pendingCallbackContinue = null;
            startupModulePath = null;
            startupModuleContinuePending = false;
            callbackPairing.MarkTerminated();
            CurrentStop = new StopInfo(StopReason.ProcessExit, Target.ProcessId, 0, null, null);
            if (domain.State != DebugSessionState.Terminated && domain.State != DebugSessionState.Failed) domain.MarkTerminated("process_exited");
            ClearModules();
            return;
        }

        callbackPairing.RecordCallbackStop(envelope.Sequence);
        try
        {
            startupModuleContinuePending = startupModulePath is not null &&
                envelope.EventArgs is LoadModuleCorDebugManagedCallbackEventArgs loaded &&
                string.Equals(Path.GetFullPath(loaded.Module.Name), startupModulePath, StringComparison.OrdinalIgnoreCase);
            if (!suppressContinueFailure)
            {
                HandleAppDomainCallback(envelope);
                if (HandleBreakpointCallback(envelope) || HandleExceptionCallback(envelope)) return;
            }
            // Cleanup must still forget invalidated native bindings before its final Dispose.
            // Do not process loads, bind symbols or publish stops while draining for Detach.
            else if (envelope.EventArgs is UnloadModuleCorDebugManagedCallbackEventArgs)
                HandleBreakpointCallback(envelope);
            else if (envelope.EventArgs is ExitAppDomainCorDebugManagedCallbackEventArgs)
                HandleAppDomainCallback(envelope);
            ContinueCallback(() => envelope.Controller.Continue(false));
        }
        catch (Exception)
        {
            if (!suppressContinueFailure)
            {
                // Preserve the single outstanding stop when command-thread processing fails.
                pendingEntryController = envelope.Controller;
                throw;
            }
            pendingCallbackContinue = envelope;
        }
    }

    private void ContinueCallback(Action resume)
    {
        callbackPairing.Continue(resume);
        if (startupModuleContinuePending) startupModulePath = null;
        startupModuleContinuePending = false;
    }

    private bool HasTargetExited()
    {
        return targetLifetime.HasExited;
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
