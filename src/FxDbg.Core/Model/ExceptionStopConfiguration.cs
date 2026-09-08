using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FxDbg.Core.Errors;

namespace FxDbg.Core.Model;

public enum ExceptionTypeMatchKind { Exact, Namespace, Derived }

public sealed class ExceptionTypeRule
{
    public ExceptionTypeRule(ExceptionTypeMatchKind kind, string typeName)
    {
        if (!Enum.IsDefined(typeof(ExceptionTypeMatchKind), kind) || string.IsNullOrWhiteSpace(typeName) ||
            typeName.Length > 1024 || typeName != typeName.Trim() || typeName.Any(char.IsControl) || typeName.IndexOf('*') >= 0 ||
            kind == ExceptionTypeMatchKind.Namespace && typeName.EndsWith(".", StringComparison.Ordinal))
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Invalid exception type rule.");
        Kind = kind; TypeName = typeName;
    }
    public ExceptionTypeMatchKind Kind { get; }
    public string TypeName { get; }

    public static ExceptionTypeRule Parse(string text)
    {
        int separator = text?.IndexOf(':') ?? -1;
        if (separator < 1) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Exception rule requires exact:, namespace: or derived: and a type name.");
        ExceptionTypeMatchKind kind = text!.Substring(0, separator) switch
        {
            "exact" => ExceptionTypeMatchKind.Exact, "namespace" => ExceptionTypeMatchKind.Namespace, "derived" => ExceptionTypeMatchKind.Derived,
            _ => throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Unknown exception type match kind.")
        };
        return new ExceptionTypeRule(kind, text.Substring(separator + 1));
    }
}

/// <summary>Null rules preserve the legacy all-first-chance switch; an explicit empty list disables it.</summary>
public sealed class ExceptionStopConfiguration
{
    public ExceptionStopConfiguration(bool firstChance, IReadOnlyList<ExceptionTypeRule>? rules = null)
    {
        if (rules is not null && (rules.Count > 64 || rules.Any(rule => rule is null)))
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "At most 64 non-null exception rules are allowed.");
        FirstChance = firstChance && (rules is null || rules.Count != 0);
        Rules = !FirstChance ? Array.Empty<ExceptionTypeRule>() : rules is null ? null : new ReadOnlyCollection<ExceptionTypeRule>(rules.ToArray());
    }
    public bool FirstChance { get; }
    public IReadOnlyList<ExceptionTypeRule>? Rules { get; }

    public static IReadOnlyList<ExceptionTypeRule> ParseRules(string text)
    {
        if (text is null || text.Length > 66560) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Exception rules text exceeds its limit.");
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ExceptionTypeRule>();
        string[] parts = text.Split(new[] { ';' }, 65);
        if (parts.Length > 64) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "At most 64 exception rules are allowed.");
        return parts.Select(part => ExceptionTypeRule.Parse(part.Trim())).ToArray();
    }

    public bool Matches(IEnumerable<string> hierarchy)
    {
        if (!FirstChance) return false;
        if (Rules is null) return true;
        bool derived = Rules.Any(rule => rule.Kind == ExceptionTypeMatchKind.Derived);
        int depth = 0;
        foreach (string type in hierarchy)
        {
            if (++depth > 128) throw new FxDbgException(FxDbgErrorCode.ValueUnavailable, "Exception type hierarchy exceeds its limit.");
            foreach (ExceptionTypeRule rule in Rules)
            {
                if (rule.Kind == ExceptionTypeMatchKind.Derived && type == rule.TypeName) return true;
                if (depth != 1) continue;
                if (rule.Kind == ExceptionTypeMatchKind.Exact && type == rule.TypeName) return true;
                if (rule.Kind == ExceptionTypeMatchKind.Namespace && type.StartsWith(rule.TypeName + ".", StringComparison.Ordinal)) return true;
            }
            if (!derived) break;
        }
        return false;
    }
}
