using System;
using System.Collections.Generic;
using FxDbg.Core.Model;

namespace FxDbg.Core.Breakpoints;

public interface ISourceBreakpointModule
{
    string Id { get; }
    string? AppDomainId { get; }
    SourceBreakpointResolution Resolve(SourceLocation location);
    ISourceBreakpointBinding Bind(BreakpointId breakpointId, BreakpointBindingLocation location);
}

public interface ISourceBreakpointBinding : IDisposable
{
    void SetEnabled(bool enabled);
}

public sealed class BreakpointBindingLocation
{
    public BreakpointBindingLocation(int methodToken, int ilOffset, SourceLocation source)
    {
        MethodToken = methodToken;
        IlOffset = ilOffset;
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public int MethodToken { get; }
    public int IlOffset { get; }
    public SourceLocation Source { get; }
}

public sealed class SourceBreakpointResolution
{
    public SourceBreakpointResolution(bool documentFound, IReadOnlyList<BreakpointBindingLocation> locations, string? diagnostic)
    {
        DocumentFound = documentFound;
        Locations = locations ?? throw new ArgumentNullException(nameof(locations));
        Diagnostic = diagnostic;
    }

    public bool DocumentFound { get; }
    public IReadOnlyList<BreakpointBindingLocation> Locations { get; }
    public string? Diagnostic { get; }
}

public sealed class BreakpointChange
{
    public BreakpointChange(BreakpointInfo breakpoint, bool removed = false)
    {
        Breakpoint = breakpoint;
        Removed = removed;
    }

    public BreakpointInfo Breakpoint { get; }
    public bool Removed { get; }
}
