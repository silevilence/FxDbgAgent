using System;
using System.Collections.Generic;
using FxDbg.Core.Errors;
using FxDbg.Core.Events;
using FxDbg.Core.Model;

namespace FxDbg.Core.Sessions;

public sealed class DebugSession
{
    private readonly List<EngineEvent> events = new();
    private long eventSequence;

    public DebugSession(SessionId id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        State = DebugSessionState.Created;
        Append(new SessionStateChangedEvent(
            id,
            ++eventSequence,
            DateTimeOffset.UtcNow,
            null,
            State,
            "session_created"));
    }

    public SessionId Id { get; }

    public DebugSessionState State { get; private set; }

    public IReadOnlyList<EngineEvent> Events => events;

    public void RecordModuleChange(ModuleChangeKind change, ModuleInfo module)
        => Append(new ModuleChangedEvent(Id, ++eventSequence, DateTimeOffset.UtcNow, change, module));
    public void RecordAppDomainChange(AppDomainChangeKind change, AppDomainInfo appDomain)
        => Append(new AppDomainChangedEvent(Id, ++eventSequence, DateTimeOffset.UtcNow, change, appDomain));
    public void RecordThreadChange(ThreadChangeKind change, ManagedThreadInfo thread)
        => Append(new ThreadChangedEvent(Id, ++eventSequence, DateTimeOffset.UtcNow, change, thread));

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

        var stopped = new StoppedEvent(Id, ++eventSequence, DateTimeOffset.UtcNow, stop);
        Append(stopped);
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
            ++eventSequence,
            DateTimeOffset.UtcNow,
            previous,
            State,
            reason);
        Append(changed);
        return changed;
    }

    private void Append(EngineEvent value)
    {
        if (events.Count == 10000) events.RemoveRange(0, 5000);
        events.Add(value);
    }
}
