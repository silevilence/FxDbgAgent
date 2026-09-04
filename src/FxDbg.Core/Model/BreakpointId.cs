using System;

namespace FxDbg.Core.Model;

public sealed class BreakpointId : IEquatable<BreakpointId>
{
    public BreakpointId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A breakpoint ID is required.", nameof(value))
            : value;
    }

    public string Value { get; }

    public static BreakpointId New() => new(Guid.NewGuid().ToString("D"));

    public bool Equals(BreakpointId? other) => other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as BreakpointId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(BreakpointId? left, BreakpointId? right) => Equals(left, right);

    public static bool operator !=(BreakpointId? left, BreakpointId? right) => !Equals(left, right);
}
