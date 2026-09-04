using System;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Events;

public sealed class BreakpointChangedEvent : EngineEvent
{
    public BreakpointChangedEvent(SessionId sessionId, long sequence, DateTimeOffset occurredAtUtc, BreakpointInfo breakpoint)
        : base(sessionId, sequence, occurredAtUtc)
    {
        Breakpoint = breakpoint ?? throw new ArgumentNullException(nameof(breakpoint));
    }

    public BreakpointInfo Breakpoint { get; }
}

public enum ThreadChangeKind
{
    Created,
    Exited,
    Updated
}

public sealed class ThreadChangedEvent : EngineEvent
{
    public ThreadChangedEvent(
        SessionId sessionId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        ThreadChangeKind kind,
        ManagedThreadInfo thread)
        : base(sessionId, sequence, occurredAtUtc)
    {
        Kind = kind;
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
    }

    public ThreadChangeKind Kind { get; }

    public ManagedThreadInfo Thread { get; }
}

public sealed class ExceptionRaisedEvent : EngineEvent
{
    public ExceptionRaisedEvent(
        SessionId sessionId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        ExceptionInfo exception,
        bool isUnhandled)
        : base(sessionId, sequence, occurredAtUtc)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        IsUnhandled = isUnhandled;
    }

    public ExceptionInfo Exception { get; }

    public bool IsUnhandled { get; }
}
