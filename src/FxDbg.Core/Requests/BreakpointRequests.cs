using System;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Requests;

public sealed class SetBreakpointRequest : EngineRequest
{
    public SetBreakpointRequest(SessionId sessionId, SourceLocation location, TimeSpan timeout)
        : base(sessionId, timeout)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public SourceLocation Location { get; }
}

public sealed class RemoveBreakpointRequest : EngineRequest
{
    public RemoveBreakpointRequest(SessionId sessionId, BreakpointId breakpointId, TimeSpan timeout)
        : base(sessionId, timeout)
    {
        BreakpointId = breakpointId ?? throw new ArgumentNullException(nameof(breakpointId));
    }

    public BreakpointId BreakpointId { get; }
}

public sealed class SetBreakpointEnabledRequest : EngineRequest
{
    public SetBreakpointEnabledRequest(
        SessionId sessionId,
        BreakpointId breakpointId,
        bool enabled,
        TimeSpan timeout)
        : base(sessionId, timeout)
    {
        BreakpointId = breakpointId ?? throw new ArgumentNullException(nameof(breakpointId));
        Enabled = enabled;
    }

    public BreakpointId BreakpointId { get; }

    public bool Enabled { get; }
}
