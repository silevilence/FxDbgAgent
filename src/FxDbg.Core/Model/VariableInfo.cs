using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FxDbg.Core.Model;

public sealed class VariableInfo
{
    private static readonly IReadOnlyList<VariableInfo> NoChildren =
        new ReadOnlyCollection<VariableInfo>(Array.Empty<VariableInfo>());

    private VariableInfo(
        string name,
        VariableKind kind,
        VariableStatus status,
        string? typeName,
        string? displayValue,
        VariableReferenceId? referenceId,
        IReadOnlyList<VariableInfo> children,
        int totalMembers,
        string? diagnostic)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A variable name is required.", nameof(name))
            : name;
        Kind = kind;
        Status = status;
        TypeName = typeName;
        DisplayValue = displayValue;
        ReferenceId = referenceId;
        Children = children;
        TotalMembers = totalMembers;
        Diagnostic = diagnostic;
    }

    public string Name { get; }

    public VariableKind Kind { get; }

    public VariableStatus Status { get; }

    public string? TypeName { get; }

    public string? DisplayValue { get; }

    public VariableReferenceId? ReferenceId { get; }

    public IReadOnlyList<VariableInfo> Children { get; }

    public int TotalMembers { get; }

    public string? Diagnostic { get; }

    public bool HasChildren => Status == VariableStatus.Available && TotalMembers > 0;

    public static VariableInfo Available(
        string name,
        VariableKind kind,
        string typeName,
        string displayValue,
        VariableReferenceId? referenceId = null,
        IReadOnlyList<VariableInfo>? children = null,
        int totalMembers = 0)
    {
        if (totalMembers < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalMembers));
        }

        IReadOnlyList<VariableInfo> snapshot = children is null
            ? NoChildren
            : new ReadOnlyCollection<VariableInfo>(new List<VariableInfo>(children));
        return new VariableInfo(
            name,
            kind,
            VariableStatus.Available,
            typeName,
            displayValue,
            referenceId,
            snapshot,
            totalMembers,
            null);
    }

    public static VariableInfo Null(string name, VariableKind kind, string typeName)
    {
        return new VariableInfo(name, kind, VariableStatus.Null, typeName, "null", null, NoChildren, 0, null);
    }

    public static VariableInfo Unavailable(string name, VariableKind kind, string diagnostic)
    {
        return new VariableInfo(
            name,
            kind,
            VariableStatus.Unavailable,
            null,
            null,
            null,
            NoChildren,
            0,
            string.IsNullOrWhiteSpace(diagnostic)
                ? throw new ArgumentException("A diagnostic is required.", nameof(diagnostic))
                : diagnostic);
    }

    public static VariableInfo OptimizedAway(string name, VariableKind kind)
    {
        return new VariableInfo(
            name,
            kind,
            VariableStatus.OptimizedAway,
            null,
            null,
            null,
            NoChildren,
            0,
            "optimized_away");
    }
}
