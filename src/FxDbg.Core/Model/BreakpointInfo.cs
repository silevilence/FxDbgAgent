using System;

namespace FxDbg.Core.Model;

public enum BreakpointState
{
    Pending,
    Verified,
    Moved,
    Unresolved
}

public sealed class BreakpointInfo
{
    public BreakpointInfo(
        BreakpointId breakpointId,
        SourceLocation requestedLocation,
        SourceLocation? boundLocation,
        BreakpointState state,
        bool enabled,
        string? diagnostic)
    {
        BreakpointId = breakpointId ?? throw new ArgumentNullException(nameof(breakpointId));
        RequestedLocation = requestedLocation ?? throw new ArgumentNullException(nameof(requestedLocation));
        BoundLocation = boundLocation;
        State = state;
        Enabled = enabled;
        Diagnostic = diagnostic;
    }

    public BreakpointId BreakpointId { get; }

    public SourceLocation RequestedLocation { get; }

    public SourceLocation? BoundLocation { get; }

    public BreakpointState State { get; }

    public bool Enabled { get; }

    public string? Diagnostic { get; }
}
