using System;
using System.Collections.Generic;
using System.Linq;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;

namespace FxDbg.Core.Variables;

/// <summary>One stop's bounded variable views and reference identities.</summary>
public sealed class VariableReader
{
    private readonly string scope;
    private readonly Dictionary<string, VariableReferenceId> identities = new(StringComparer.Ordinal);
    private readonly Dictionary<VariableReferenceId, IVariableValue> references = new();

    public VariableReader(string scope) => this.scope = scope;

    public IReadOnlyList<VariableInfo> Read(IReadOnlyList<VariableMember> members, int maxDepth, int maxMembers, int maxStringLength)
    {
        Validate(maxDepth, maxMembers, maxStringLength);
        VariableMember[] roots = members.Take(maxMembers).ToArray();
        int remaining = maxMembers - roots.Length;
        var results = new List<VariableInfo>();
        foreach (VariableMember member in roots)
            results.Add(ReadOne(member, maxDepth, maxStringLength, new HashSet<string>(StringComparer.Ordinal), ref remaining));
        return results;
    }

    public IReadOnlyList<VariableInfo> Expand(VariableReferenceId referenceId, int start, int count, int maxDepth, int maxStringLength)
    {
        Validate(maxDepth, count, maxStringLength);
        if (start < 0) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Variable page start must be nonnegative.");
        if (!references.TryGetValue(referenceId, out IVariableValue? value))
            throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Variable reference is unknown or belongs to an earlier stop.");
        if (start >= value.TotalMembers) return Array.Empty<VariableInfo>();
        IReadOnlyList<VariableMember> members = value.GetMembers(start, Math.Min(count, value.TotalMembers - start));
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        if (value.ReferenceIdentity is not null) ancestors.Add(value.ReferenceIdentity);
        int remaining = count - members.Count;
        return members.Select(member => ReadOne(member, maxDepth, maxStringLength, ancestors, ref remaining)).ToArray();
    }

    private VariableInfo ReadOne(VariableMember member, int depth, int maxStringLength, HashSet<string> ancestors, ref int remaining)
    {
        try
        {
            IVariableValue value = member.Value;
            if (value.Status == VariableStatus.Null) return VariableInfo.Null(member.Name, member.Kind, value.TypeName);
            if (value.Status == VariableStatus.OptimizedAway) return VariableInfo.OptimizedAway(member.Name, member.Kind);
            if (value.Status == VariableStatus.Unavailable) return VariableInfo.Unavailable(member.Name, member.Kind, value.Diagnostic ?? "Value is unavailable.");
            string display = value.Format(maxStringLength);
            VariableReferenceId? reference = null;
            string? identity = value.ReferenceIdentity;
            if (identity is not null)
            {
                if (!identities.TryGetValue(identity, out reference))
                {
                    if (identities.Count == 10000) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "This stop's reference limit has been reached.");
                    reference = new VariableReferenceId(scope + ":" + (identities.Count + 1));
                    identities.Add(identity, reference);
                    references.Add(reference, value);
                }
            }
            var children = new List<VariableInfo>();
            bool cyclic = identity is not null && ancestors.Contains(identity);
            if (depth > 0 && remaining > 0 && !cyclic && value.TotalMembers > 0)
            {
                IReadOnlyList<VariableMember> members = value.GetMembers(0, Math.Min(remaining, value.TotalMembers));
                remaining -= members.Count;
                if (identity is not null) ancestors.Add(identity);
                foreach (VariableMember child in members) children.Add(ReadOne(child, depth - 1, maxStringLength, ancestors, ref remaining));
                if (identity is not null) ancestors.Remove(identity);
            }
            return VariableInfo.Available(member.Name, member.Kind, value.TypeName, display, reference, children, value.TotalMembers);
        }
        catch (FxDbgException exception) when (exception.Code == FxDbgErrorCode.ValueOptimizedAway || exception.Code == FxDbgErrorCode.ValueUnavailable)
        {
            return exception.Code == FxDbgErrorCode.ValueOptimizedAway ? VariableInfo.OptimizedAway(member.Name, member.Kind)
                : VariableInfo.Unavailable(member.Name, member.Kind, exception.Message);
        }
    }

    public static void Validate(int depth, int members, int stringLength)
    {
        if (depth < 0 || depth > 8 || members <= 0 || members > 1024 || stringLength <= 0 || stringLength > 32768)
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Variable limits: depth 0-8, members 1-1024, string length 1-32768.");
    }
}
