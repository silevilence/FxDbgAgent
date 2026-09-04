using System;

namespace FxDbg.Core.Model;

public sealed class VariableReferenceId : IEquatable<VariableReferenceId>
{
    public VariableReferenceId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A variable reference ID is required.", nameof(value))
            : value;
    }

    public string Value { get; }

    public bool Equals(VariableReferenceId? other) => other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as VariableReferenceId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
