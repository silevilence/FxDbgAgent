using System;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Events;

public abstract class EngineEvent
{
    protected EngineEvent(SessionId sessionId, long sequence, DateTimeOffset occurredAtUtc)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        Sequence = sequence;
        OccurredAtUtc = occurredAtUtc;
    }

    public SessionId SessionId { get; }

    public long Sequence { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
