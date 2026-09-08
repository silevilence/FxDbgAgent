using System;
using System.Linq;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class ExceptionStopConfigurationTests
{
    [Theory]
    [InlineData("exact:A.Derived", true)]
    [InlineData("exact:A.Base", false)]
    [InlineData("exact:a.Derived", false)]
    [InlineData("namespace:A", true)]
    [InlineData("namespace:A.Der", false)]
    [InlineData("namespace:B", false)]
    [InlineData("derived:A.Base", true)]
    [InlineData("derived:A.Derived", true)]
    [InlineData("derived:System.Object", true)]
    [InlineData("exact:X;derived:A.Base", true)]
    public void MatchesOnlySpecifiedTypeRelations(string rules, bool expected)
        => Assert.Equal(expected, new ExceptionStopConfiguration(true, ExceptionStopConfiguration.ParseRules(rules))
            .Matches(new[] { "A.Derived", "A.Base", "System.Exception", "System.Object" }));

    [Fact]
    public void PreservesLegacySwitchAndExplicitEmptyRestoresDefault()
    {
        Assert.True(new ExceptionStopConfiguration(true).Matches(Array.Empty<string>()));
        Assert.False(new ExceptionStopConfiguration(false).FirstChance);
        Assert.False(new ExceptionStopConfiguration(true, Array.Empty<ExceptionTypeRule>()).FirstChance);
        Assert.Empty(new ExceptionStopConfiguration(false, ExceptionStopConfiguration.ParseRules("exact:X")).Rules!);
        Assert.False(new ExceptionStopConfiguration(true, ExceptionStopConfiguration.ParseRules("namespace:A" )).Matches(new[] { "AB.Error" }));
    }
    [Theory]
    [InlineData("foo:X")][InlineData("exact:")][InlineData("namespace:A.")][InlineData("exact:X*")][InlineData("exact:A; ")]
    public void RejectsMalformedRules(string text) => Assert.Equal(FxDbgErrorCode.InvalidRequest,
        Assert.Throws<FxDbgException>(() => ExceptionStopConfiguration.ParseRules(text)).Code);

    [Fact]
    public void BoundsAndCopiesConfigurationWithoutWalkingUnneededBases()
    {
        Assert.Throws<FxDbgException>(() => ExceptionStopConfiguration.ParseRules(string.Join(";", Enumerable.Repeat("exact:X",65))));
        Assert.Throws<FxDbgException>(() => ExceptionTypeRule.Parse("exact:"+new string('x',1025)));
        Assert.Throws<FxDbgException>(() => new ExceptionTypeRule((ExceptionTypeMatchKind)100,"X"));
        Assert.Throws<FxDbgException>(() => new ExceptionStopConfiguration(true,new ExceptionTypeRule[]{null!}));
        var rules = new[]{ ExceptionTypeRule.Parse("exact:X") };
        var copied = new ExceptionStopConfiguration(true,rules); rules[0] = ExceptionTypeRule.Parse("exact:Y");
        Assert.True(copied.Matches(new[]{"X"}));
        Assert.False(copied.Matches(Enumerable.Repeat("Z",1000)));
        Assert.Throws<FxDbgException>(() => new ExceptionStopConfiguration(true,ExceptionStopConfiguration.ParseRules("derived:X")).Matches(Enumerable.Repeat("Z",129)));
    }
}
