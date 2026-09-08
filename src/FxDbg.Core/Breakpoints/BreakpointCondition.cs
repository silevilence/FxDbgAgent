using System;
using System.Globalization;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;

namespace FxDbg.Core.Breakpoints;

/// <summary>Immutable validated policy. Counters belong to the logical breakpoint, not native bindings.</summary>
public sealed class BreakpointCondition
{
    private readonly RestrictedExpression? expression;
    private readonly int? threshold;
    private readonly bool atLeast;

    public BreakpointCondition(string? condition = null, string? hitCondition = null)
    {
        Condition = condition; HitCondition = hitCondition;
        if (hitCondition is not null)
        {
            atLeast = hitCondition.StartsWith(">=", StringComparison.Ordinal);
            string digits = hitCondition.Substring(atLeast ? 2 : hitCondition.StartsWith("=", StringComparison.Ordinal) ? 1 : 0);
            if ((!atLeast && !hitCondition.StartsWith("=", StringComparison.Ordinal)) || digits.Length == 0 || digits[0] == '0' ||
                !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
                throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Hit condition must be =N or >=N, with N from 1 to 2147483647.");
            threshold = value;
        }
        if (condition is not null)
        {
            using var budget = new EvaluationBudget(250);
            expression = RestrictedExpression.Parse(condition, budget);
        }
    }

    public string? Condition { get; }
    public string? HitCondition { get; }

    public bool ShouldStop(long hitCount, Func<RestrictedExpression, EvaluationBudget, ExpressionValue> evaluate, out string? diagnostic)
    {
        diagnostic = null;
        if (threshold.HasValue && (atLeast ? hitCount < threshold.Value : hitCount != threshold.Value)) return false;
        if (expression is null) return true;
        using var budget = new EvaluationBudget(250);
        try { bool result = evaluate(expression, budget).Boolean(); budget.Check(); return result; }
        catch (Exception error) when (error is FxDbgException or OperationCanceledException)
        {
            // Neither expression text nor values or native exception messages enter diagnostics.
            diagnostic = "Condition could not be evaluated; stopped conservatively (" +
                (error is FxDbgException domain ? FxDbgErrorCodeWireName.Format(domain.Code) : "timeout") + ").";
            return true;
        }
    }
}
