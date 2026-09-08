using System;
using System.Linq;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using Xunit;

namespace FxDbg.UnitTests.Breakpoints;

public sealed class BreakpointConditionTests
{
    [Theory]
    [InlineData(null, null, "1,2,3,4,5")]
    [InlineData(null, "=3", "3")]
    [InlineData(null, ">=3", "3,4,5")]
    [InlineData("iteration % 2 == 0", null, "2,4")]
    [InlineData("iteration % 2 == 0", ">=3", "4")]
    [InlineData("false", null, "")]
    [InlineData("true", "=2147483647", "")]
    public void Hit_and_expression_conditions_are_anded(string? expression, string? hits, string expected)
    {
        var policy = new BreakpointCondition(expression, hits);
        var stopped = Enumerable.Range(1,5).Where(index => policy.ShouldStop(index,
            (parsed,budget) => parsed.Evaluate((_,_) => new ExpressionValue(index),budget),out _));
        Assert.Equal(expected,string.Join(",",stopped));
    }

    [Theory]
    [InlineData("0")][InlineData("3")][InlineData("=0")][InlineData(">=0")][InlineData("=-1")]
    [InlineData("=03")][InlineData("=2147483648")][InlineData("= 3")][InlineData("=+3")][InlineData(">3")][InlineData("=")][InlineData("")]
    public void Malformed_thresholds_are_rejected(string text) => Assert.Throws<FxDbgException>(() => new BreakpointCondition(hitCondition:text));

    [Theory]
    [InlineData("1")][InlineData("missing")][InlineData("1 / 0 == 2")]
    public void Nonboolean_and_read_errors_stop_with_sanitized_diagnostics(string expression)
    {
        var policy = new BreakpointCondition(expression);
        Assert.True(policy.ShouldStop(1,(parsed,budget)=>parsed.Evaluate((_,_)=>throw new FxDbgException(FxDbgErrorCode.ValueUnavailable,"SECRET"),budget),out var diagnostic));
        Assert.Contains("stopped conservatively",diagnostic); Assert.DoesNotContain("SECRET",diagnostic);
    }

    [Fact]
    public void Unmatched_threshold_skips_reads_and_slow_reads_fail_stop_with_deadline()
    {
        var threshold = new BreakpointCondition("secret", "=3");
        Assert.False(threshold.ShouldStop(1,(_,_)=>throw new InvalidOperationException("must not read"),out var none)); Assert.Null(none);
        var policy = new BreakpointCondition("true");
        Assert.True(policy.ShouldStop(1,(_,budget)=>
        {
            Assert.True(budget.Token.WaitHandle.WaitOne(2000)); budget.Token.ThrowIfCancellationRequested(); return new ExpressionValue(true);
        },out var diagnostic));
        Assert.Contains("timeout",diagnostic);
        Assert.True(policy.ShouldStop(2,(parsed,budget)=>parsed.Evaluate((_,_)=>throw new Exception(),budget),out var recovered)); Assert.Null(recovered);
    }
}
