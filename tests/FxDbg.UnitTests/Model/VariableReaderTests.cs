using System.Collections.Generic;
using System.Linq;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Variables;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class VariableReaderTests
{
    [Fact]
    public void Member_budget_preserves_roots_and_bounds_all_descendants()
    {
        var value = new ObjectValue();
        for (int i = 0; i < 50; i++) value.Members.Add(new VariableMember("child" + i, VariableKind.InstanceField, new ObjectValue { Identity = "child" + i }));
        var roots = new[] { new VariableMember("one", VariableKind.Local, value), new VariableMember("two", VariableKind.Argument, value) };
        var result = new VariableReader("stop").Read(roots, 8, 7, 10);
        Assert.Equal(new[] { "one", "two" }, result.Select(item => item.Name));
        Assert.Equal(7, result.Count + result.Sum(item => item.Children.Count));
        Assert.Equal(50, result[0].TotalMembers);
    }

    [Fact]
    public void Expansion_pages_members_and_rejects_references_from_another_stop()
    {
        var value = new ObjectValue();
        for (int i = 0; i < 20; i++) value.Members.Add(new VariableMember("child" + i, VariableKind.InstanceField, value));
        var reader = new VariableReader("first");
        var root = Assert.Single(reader.Read(new[] { new VariableMember("root", VariableKind.Local, value) }, 0, 1, 10));
        Assert.Equal(new[] { "child18", "child19" }, reader.Expand(root.ReferenceId!, 18, 3, 0, 10).Select(item => item.Name));
        Assert.Empty(reader.Expand(root.ReferenceId!, 20, 1, 0, 10));
        Assert.Equal(FxDbgErrorCode.ValueUnavailable, Assert.Throws<FxDbgException>(() => new VariableReader("second").Expand(root.ReferenceId!, 0, 1, 0, 10)).Code);
    }

    [Theory]
    [InlineData(VariableStatus.Null)]
    [InlineData(VariableStatus.Unavailable)]
    [InlineData(VariableStatus.OptimizedAway)]
    public void Unreadable_values_preserve_their_status(VariableStatus status)
    {
        var value = new ObjectValue { Status = status };
        var result = Assert.Single(new VariableReader("stop").Read(new[] { new VariableMember("v", VariableKind.Local, value) }, 1, 10, 10));
        Assert.Equal(status, result.Status);
        Assert.Empty(result.Children);
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(9, 1, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 1025, 1)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 1, 32769)]
    public void Invalid_budgets_are_rejected(int depth, int members, int text)
        => Assert.Equal(FxDbgErrorCode.InvalidRequest, Assert.Throws<FxDbgException>(() => VariableReader.Validate(depth, members, text)).Code);

    [Fact]
    public void Cyclic_reference_is_identified_without_recursively_expanding_it()
    {
        var value = new ObjectValue();
        value.Members.Add(new VariableMember("self", VariableKind.InstanceField, value));
        var reader = new VariableReader("stop-1");
        VariableInfo result = Assert.Single(reader.Read(new[] { new VariableMember("root", VariableKind.Local, value) }, 4, 10, 10));
        VariableInfo self = Assert.Single(result.Children);
        Assert.Equal(result.ReferenceId, self.ReferenceId);
        Assert.Empty(self.Children);
    }

    private sealed class ObjectValue : IVariableValue
    {
        internal List<VariableMember> Members { get; } = new();
        public VariableStatus Status { get; set; } = VariableStatus.Available;
        public string TypeName => "Sample.Node";
        public string? Diagnostic => null;
        internal string Identity { get; set; } = "object-1";
        public string? ReferenceIdentity => Identity;
        public int TotalMembers => Members.Count;
        public string Format(int maxStringLength) => "{Sample.Node}";
        public IReadOnlyList<VariableMember> GetMembers(int start, int count) => Members.GetRange(start, System.Math.Min(count, Members.Count - start));
    }
}
