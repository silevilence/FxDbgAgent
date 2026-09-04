using System;
using System.Collections.Generic;
using FxDbg.Core.Errors;
using FxDbg.Core.Events;

namespace FxDbg.Core.Sessions;

public sealed class DebugSession
{
    private readonly List<EngineEvent> events = new();

    public DebugSession(SessionId id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        State = DebugSessionState.Created;
        events.Add(new SessionStateChangedEvent(
            id,
            1,
            DateTimeOffset.UtcNow,
            null,
            State,
            "session_created"));
    }

    public SessionId Id { get; }

    public DebugSessionState State { get; private set; }

    public IReadOnlyList<EngineEvent> Events => events;

    public SessionStateChangedEvent Start()
    {
        return ChangeState(DebugSessionState.Starting, "start_requested", DebugSessionState.Created);
    }

    public SessionStateChangedEvent MarkRunning(string reason)
    {
        return ChangeState(DebugSessionState.Running, reason, DebugSessionState.Starting);
    }

    public StoppedEvent MarkStopped(StopInfo stop)
    {
        ChangeState(
            DebugSessionState.Stopped,
            $"stopped_{stop.Reason.ToString().ToLowerInvariant()}",
            DebugSessionState.Starting,
            DebugSessionState.Running);

        var stopped = new StoppedEvent(Id, events.Count + 1, DateTimeOffset.UtcNow, stop);
        events.Add(stopped);
        return stopped;
    }

    public SessionStateChangedEvent Resume()
    {
        return ChangeState(DebugSessionState.Running, "resume_requested", DebugSessionState.Stopped);
    }

    public SessionStateChangedEvent BeginDetach()
    {
        return ChangeState(
            DebugSessionState.Detaching,
            "detach_requested",
            DebugSessionState.Starting,
            DebugSessionState.Running,
            DebugSessionState.Stopped);
    }

    public SessionStateChangedEvent MarkTerminated(string reason)
    {
        return ChangeState(
            DebugSessionState.Terminated,
            reason,
            DebugSessionState.Starting,
            DebugSessionState.Running,
            DebugSessionState.Stopped,
            DebugSessionState.Detaching);
    }

    public SessionStateChangedEvent Fail(string reason)
    {
        return ChangeState(
            DebugSessionState.Failed,
            reason,
            DebugSessionState.Created,
            DebugSessionState.Starting,
            DebugSessionState.Running,
            DebugSessionState.Stopped,
            DebugSessionState.Detaching);
    }

    private SessionStateChangedEvent ChangeState(
        DebugSessionState nextState,
        string reason,
        params DebugSessionState[] allowedStates)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A state-change reason is required.");
        }

        if (Array.IndexOf(allowedStates, State) < 0)
        {
            throw new FxDbgException(
                FxDbgErrorCode.InvalidSessionState,
                $"Cannot transition a session from {State} to {nextState}.");
        }

        DebugSessionState previous = State;
        State = nextState;
        var changed = new SessionStateChangedEvent(
            Id,
            events.Count + 1,
            DateTimeOffset.UtcNow,
            previous,
            State,
            reason);
        events.Add(changed);
        return changed;
    }
}
