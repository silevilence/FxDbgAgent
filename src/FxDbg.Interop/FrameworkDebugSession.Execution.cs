using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Events;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    private CorDebugStepper? stepper;

    public SessionId SessionId => domain.Id;
    public DebugSessionState State => domain.State;
    public StopInfo? CurrentStop { get; private set; }
    public IReadOnlyList<EngineEvent> Events => domain.Events;

    public void Pause(CancellationToken cancellationToken = default)
    {
        RequireActive();
        if (State != DebugSessionState.Running) throw InvalidState("pause");
        cancellationToken.ThrowIfCancellationRequested();
        process.Stop(0);
        callbackPairing.RecordManualStop();
        pendingEntryController = process;
        StopAt(process.Threads.FirstOrDefault(), StopReason.UserPause);
    }

    public void Step(int threadId, StepKind kind, CancellationToken cancellationToken = default)
    {
        RequireStopped();
        if (threadId <= 0 || !Enum.IsDefined(typeof(StepKind), kind))
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Step requires a positive thread ID and into, over, or out.");
        cancellationToken.ThrowIfCancellationRequested();
        CorDebugThread thread = RequireThread(threadId);
        CorDebugFrame frame = thread.ActiveFrame;
        if (frame is null || frame.Raw is not ICorDebugILFrame raw)
            throw new FxDbgException(FxDbgErrorCode.FrameNotFound, "The selected thread has no active managed IL frame to step.");
        stepper = frame.CreateStepper();
        try
        {
            stepper.SetRangeIL(true);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            if (kind == StepKind.Out) stepper.StepOut();
            else
            {
                var ilFrame = new CorDebugILFrame(raw);
                int offset = ilFrame.IP.pnOffset;
                CorDebugFunction function = frame.Function;
                int end = function.ILCode.Size;
                if (modules.TryGetValue(function.Module.Raw, out DebugModule? module))
                    end = module.GetStepRangeEnd(unchecked((int)function.Token.Value), offset, end);
                if (end <= offset) stepper.Step(kind == StepKind.Into);
                else stepper.StepRange(kind == StepKind.Into,
                    new[] { new COR_DEBUG_STEP_RANGE { startOffset = offset, endOffset = end } }, 1);
            }
            Continue();
        }
        catch
        {
            stepper?.Deactivate();
            stepper = null;
            throw;
        }
    }

    public StopInfo WaitForStop(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfWrongThread();
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive wait timeout is required.");
        // ExitProcess may have been handled by the idle pump before this request was scheduled.
        if (HasExited) return CurrentStop!;
        RequireActive();
        var timer = Stopwatch.StartNew();
        while (!IsStopped && !HasExited)
        {
            if (cancellationToken.IsCancellationRequested || timer.Elapsed >= timeout)
            {
                Pause();
                throw new FxDbgException(cancellationToken.IsCancellationRequested
                    ? FxDbgErrorCode.OperationCancelled : FxDbgErrorCode.OperationTimedOut,
                    "Execution wait ended; the target has been paused and can be inspected or continued.");
            }
            PumpNextCallback(TimeSpan.FromMilliseconds(10), CancellationToken.None);
        }
        return CurrentStop!;
    }

    public void Detach()
    {
        RequireActive();
        Dispose();
    }

    public void Terminate(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        RequireActive();
        if (!Target.LaunchedByDebugger)
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "An attached target cannot be terminated. Use detach instead.");
        if (timeout <= TimeSpan.Zero) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive termination timeout is required.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsStopped) Pause(cancellationToken);
        process.Terminate(0);
        // Desktop CLR Terminate consumes the native stop and delivers ExitProcess directly.
        // A subsequent Continue returns CORDBG_E_SUPERFLOUS_CONTINUE on the supported runtime.
        // Keep the terminal stop out of the resume path and confirm ExitProcess before reporting success.
        pendingEntryController = null;
        stepper = null;
        WaitForExitProcess(DateTime.UtcNow.Add(timeout));
    }

    private void StopAt(CorDebugThread? thread, StopReason reason, bool unhandledException = true)
    {
        stopGeneration++;
        framesById.Clear();
        variableReader = null;
        if (stepper is not null)
        {
            stepper.Deactivate();
            stepper = null;
        }
        int threadId = thread?.Id ?? 0;
        StoppedThreadId = threadId == 0 ? null : threadId;
        string? appDomain = thread?.AppDomain.Name;
        IReadOnlyList<StackFrameInfo> stack = thread is null ? Array.Empty<StackFrameInfo>() : CaptureStack(thread, 0, 5);
        StackFrameInfo? first = stack.FirstOrDefault();
        ExceptionInfo? exception = reason == StopReason.Exception && thread is not null
            ? CaptureException(thread, unhandledException) : null;
        CurrentStop = new StopInfo(reason, Target.ProcessId, threadId, appDomain, first?.SourceLocation,
            HitBreakpointId, exception, moduleName: first?.ModuleName, methodName: first?.MethodName, briefStack: stack);
        domain.MarkStopped(CurrentStop);
    }

    private CorDebugThread RequireThread(int threadId)
    {
        if (process.TryGetThread(threadId, out CorDebugThread thread) != HRESULT.S_OK)
            throw new FxDbgException(FxDbgErrorCode.ThreadNotFound, "Managed thread does not exist: " + threadId);
        return thread;
    }

    private void RequireActive()
    {
        ThrowIfWrongThread();
        if (disposed || State == DebugSessionState.Terminated || State == DebugSessionState.Failed)
            throw InvalidState("operate on");
    }

    private void RequireStopped()
    {
        RequireActive();
        if (State != DebugSessionState.Stopped) throw InvalidState("continue, step, or inspect");
    }

    private FxDbgException InvalidState(string operation) => new(FxDbgErrorCode.InvalidSessionState,
        "Cannot " + operation + " a session in state " + State + ".");
}
