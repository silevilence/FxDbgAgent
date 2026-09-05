using FxDbg.Cli;
using FxDbg.Core.Errors;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class CliCommandTests
{
    [Fact]
    public void Windows_argument_string_preserves_quotes_and_empty_arguments()
    {
        var command = CliCommand.Parse(new[] { "launch", "--exe", "sample.exe", "--args", "\"space argument\" \"\" --flag" });
        Assert.Equal(new[] { "space argument", "", "--flag" }, command.Parameters["arguments"]!.ToObject<string[]>());
    }
    [Fact]
    public void Launch_preserves_repeated_arguments_and_environment()
    {
        var command = CliCommand.Parse(new[] { "launch", "--exe", "sample.exe", "--arg", "space argument", "--arg", "--literal", "--env", "NAME=a=b", "--stop-at-entry" });
        Assert.Equal("launch", command.Method);
        Assert.Equal(new[] { "space argument", "--literal" }, command.Parameters["arguments"]!.ToObject<string[]>());
        Assert.Equal("a=b", (string?)command.Parameters["environment"]?["NAME"]);
        Assert.True((bool)command.Parameters["stopAtEntry"]!);
    }

    [Theory]
    [InlineData("unknown", "--session", "b8ba588d-d261-45aa-8cb0-68d63bd773a0")]
    [InlineData("continue", "--thread", "1")]
    [InlineData("launch", "--exe", "")]
    public void Invalid_commands_fail_before_starting_a_Host(params string[] args)
        => Assert.Equal(FxDbgErrorCode.InvalidRequest, Assert.Throws<FxDbgException>(() => CliCommand.Parse(args)).Code);
}
