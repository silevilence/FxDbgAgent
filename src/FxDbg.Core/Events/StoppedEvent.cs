using System;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Events;

public sealed class StoppedEvent : EngineEvent
{
    public StoppedEvent(SessionId sessionId, long sequence, DateTimeOffset occurredAtUtc, StopInfo stop)
        : base(sessionId, sequence, occurredAtUtc)
    {
        Stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    public StopInfo Stop { get; }
}
