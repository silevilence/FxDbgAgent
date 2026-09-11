using System;
using System.Linq;
using System.Threading;
using FxDbg.Core.Errors;
using FxDbg.Core.Evaluation;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class MemberSuggestionsTests
{
    [Fact]
    public void Suggestions_rank_prefix_case_and_distance_then_ordinal_with_eight_distinct_names()
    {
        using var budget = new EvaluationBudget(1000);
        Assert.Equal(" Candidate members: Count, Counter, count, Coint, Mount.",
            MemberSuggestions.Suffix("Count", new[] { "Mount", "count", "Counter", "Count", "Coint", "Count", "Unrelated" }, budget));
        Assert.Equal(" Candidate members: Pure.", MemberSuggestions.Suffix("Puer", new[] { "Pure", "SideEffect" }, budget));
        Assert.Equal(" Candidate members: P0, P1, P2, P3, P4, P5, P6, P7.",
            MemberSuggestions.Suffix("P", Enumerable.Range(0, 10).Reverse().Select(index => "P" + index), budget));
        Assert.Equal(string.Empty, MemberSuggestions.Suffix("Unrelated", new[] { "Count" }, budget));
    }

    [Fact]
    public void Only_safe_bounded_metadata_identifiers_are_emitted_never_requested_text()
    {
        using var budget = new EvaluationBudget(1000);
        Assert.Equal(string.Empty, MemberSuggestions.Suffix("secret-expression-value", new[] { "Field", "Property" }, budget));
        Assert.Equal(string.Empty, MemberSuggestions.Suffix("P", new[] { "P\nsecret", "P.secret", "P<secret>", "1P", "", new string('P', 65) }, budget));
        Assert.Equal(string.Empty, MemberSuggestions.Suffix(new string('P', 65), new[] { "Pure" }, budget));
        Assert.Equal(string.Empty, MemberSuggestions.Suffix("", new[] { "Pure" }, budget));
        Assert.Equal(" Candidate members: 名称1.", MemberSuggestions.Suffix("名称", new[] { "名称1" }, budget));
    }

    [Fact]
    public void Work_shares_step_and_cancellation_budgets_even_for_duplicate_names()
    {
        using var budget = new EvaluationBudget(1000);
        var error = Assert.Throws<FxDbgException>(() => MemberSuggestions.Suffix("Puer", Enumerable.Repeat("Pure", 3000), budget));
        Assert.Equal(FxDbgErrorCode.ExpressionLimitExceeded, error.Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var stopped = new EvaluationBudget(1000, cancelled.Token);
        Assert.Equal(FxDbgErrorCode.OperationCancelled, Assert.Throws<FxDbgException>(() => MemberSuggestions.Suffix("P", new[] { "Pure" }, stopped)).Code);
    }
}
