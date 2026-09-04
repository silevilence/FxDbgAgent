using FxDbg.Core.Model;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class VariableInfoTests
{
    [Fact]
    public void Result_factories_keep_null_unavailable_and_optimized_away_distinct()
    {
        VariableInfo nullValue = VariableInfo.Null("customer", VariableKind.Local, "Customer");
        VariableInfo unavailable = VariableInfo.Unavailable("customer", VariableKind.Local, "metadata_missing");
        VariableInfo optimizedAway = VariableInfo.OptimizedAway("customer", VariableKind.Local);

        Assert.Equal(VariableStatus.Null, nullValue.Status);
        Assert.Equal(VariableStatus.Unavailable, unavailable.Status);
        Assert.Equal("metadata_missing", unavailable.Diagnostic);
        Assert.Equal(VariableStatus.OptimizedAway, optimizedAway.Status);
        Assert.All(new[] { nullValue, unavailable, optimizedAway }, item => Assert.False(item.HasChildren));
    }
}
