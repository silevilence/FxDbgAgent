using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
        string? diagnostic, string? appDomainId = null, IReadOnlyList<string>? boundAppDomainIds = null)
    {
        BreakpointId = breakpointId ?? throw new ArgumentNullException(nameof(breakpointId));
        RequestedLocation = requestedLocation ?? throw new ArgumentNullException(nameof(requestedLocation));
        BoundLocation = boundLocation;
        State = state;
        Enabled = enabled;
        Diagnostic = diagnostic;
        AppDomainId = appDomainId;
        BoundAppDomainIds = new ReadOnlyCollection<string>(boundAppDomainIds is null ? Array.Empty<string>() : new List<string>(boundAppDomainIds));
    }

    public BreakpointId BreakpointId { get; }

    public SourceLocation RequestedLocation { get; }

    public SourceLocation? BoundLocation { get; }

    public BreakpointState State { get; }

    public bool Enabled { get; }

    public string? Diagnostic { get; }
    public string? AppDomainId { get; }
    public IReadOnlyList<string> BoundAppDomainIds { get; }
}
