using System;

namespace FxDbg.Core.Model;

public sealed class FrameId : IEquatable<FrameId>
{
    public FrameId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A frame ID is required.", nameof(value))
            : value;
    }

    public string Value { get; }

    public bool Equals(FrameId? other) => other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as FrameId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
