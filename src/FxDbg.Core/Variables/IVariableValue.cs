using System;
using System.Collections.Generic;
using FxDbg.Core.Model;

namespace FxDbg.Core.Variables;

/// <summary>Read-only value access. Implementations must never evaluate target code.</summary>
public interface IVariableValue
{
    VariableStatus Status { get; }
    string TypeName { get; }
    string? Diagnostic { get; }
    string? ReferenceIdentity { get; }
    int TotalMembers { get; }
    string Format(int maxStringLength);
    IReadOnlyList<VariableMember> GetMembers(int start, int count);
}

public sealed class VariableMember
{
    public VariableMember(string name, VariableKind kind, IVariableValue value)
    {
        Name = name;
        Kind = kind;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
    public string Name { get; }
    public VariableKind Kind { get; }
    public IVariableValue Value { get; }
}
