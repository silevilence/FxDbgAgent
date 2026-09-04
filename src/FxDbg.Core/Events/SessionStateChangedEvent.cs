using System;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Events;

public sealed class SessionStateChangedEvent : EngineEvent
{
    public SessionStateChangedEvent(
        SessionId sessionId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        DebugSessionState? previousState,
        DebugSessionState currentState,
        string reason)
        : base(sessionId, sequence, occurredAtUtc)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    public DebugSessionState? PreviousState { get; }

    public DebugSessionState CurrentState { get; }

    public string Reason { get; }
}
