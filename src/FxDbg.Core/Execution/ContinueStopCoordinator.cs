using System;
using FxDbg.Core.Errors;

namespace FxDbg.Core.Execution;

public sealed class ContinueStopCoordinator
{
    private long lastCallbackSequence;

    public ContinueStopCoordinator(TargetExecutionState initialState)
    {
        State = initialState;
        StopCount = initialState == TargetExecutionState.Stopped ? 1 : 0;
    }

    public TargetExecutionState State { get; private set; }

    public long StopCount { get; private set; }

    public long ContinueCount { get; private set; }

    public long OutstandingStopCount => StopCount - ContinueCount;

    public void RecordCallbackStop(long sequence)
    {
        if (sequence <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive callback sequence is required.");
        }

        if (sequence <= lastCallbackSequence)
        {
            throw new FxDbgException(
                FxDbgErrorCode.InvalidRequest,
                $"Stop callback sequence {sequence} has already been consumed.");
        }

        RecordStop("callback " + sequence);
        lastCallbackSequence = sequence;
    }

    public void RecordManualStop()
    {
        RecordStop("manual stop");
    }

    public void Continue(Action continueTarget)
    {
        if (continueTarget is null)
        {
            throw new ArgumentNullException(nameof(continueTarget));
        }

        if (State != TargetExecutionState.Stopped || OutstandingStopCount != 1)
        {
            throw new FxDbgException(
                FxDbgErrorCode.InvalidSessionState,
                $"Cannot continue while the target is {State} with {OutstandingStopCount} outstanding stops.");
        }

        continueTarget();
        ContinueCount++;
        State = TargetExecutionState.Running;
    }

    public void MarkTerminated()
    {
        State = TargetExecutionState.Terminated;
    }

    private void RecordStop(string description)
    {
        if (State != TargetExecutionState.Running)
        {
            throw new FxDbgException(
                FxDbgErrorCode.InvalidSessionState,
                $"Cannot record {description} while the target is {State}.");
        }

        StopCount++;
        State = TargetExecutionState.Stopped;
    }
}
