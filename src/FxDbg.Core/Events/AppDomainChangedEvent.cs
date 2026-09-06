using System;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Events;

public enum AppDomainChangeKind { Created, Updated, Exited }
public sealed class AppDomainChangedEvent : EngineEvent
{
    public AppDomainChangedEvent(SessionId sessionId, long sequence, DateTimeOffset occurredAtUtc, AppDomainChangeKind change, AppDomainInfo appDomain)
        : base(sessionId, sequence, occurredAtUtc) { Change = change; AppDomain = appDomain; }
    public AppDomainChangeKind Change { get; }
    public AppDomainInfo AppDomain { get; }
}
