using System;
using System.IO;
using FxDbg.Validation;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Opt-in measurement of the same product suites. No replacement debugger or instrumented FX target.
public sealed class RealStage4Coverage
{
    [Theory]
    [InlineData("native", "evaluation")]
    [InlineData("native", "exception-filters")]
    [InlineData("native", "conditional-breakpoints")]
    [InlineData("native", "queued-unload-detach")]
    [InlineData("mcp", "evaluation")]
    [InlineData("mcp", "exception-filters")]
    [InlineData("mcp", "conditional-breakpoints")]
    [InlineData("dap-cli", "evaluation")]
    [InlineData("dap-cli", "exception-filters")]
    [InlineData("dap-cli", "conditional-breakpoints")]
    [InlineData("protocol", "mcp")]
    public void Existing_real_matrix_passes_under_coverage(string entry, string suite)
    {
        string Required(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException("Missing " + key);
        string root = Path.GetFullPath(Required("FXDBG_COVERAGE_ROOT")), configuration = Required("FXDBG_COVERAGE_CONFIGURATION");
        Assert.Contains(configuration, new[] { "Debug", "Release" });
        string logs = Path.Combine(root, "artifacts/stage4-coverage", configuration); Directory.CreateDirectory(logs);
        void Run(string executable, string[] arguments, string suffix = "")
        {
            string log = Path.Combine(logs, entry + "-" + suite + suffix + ".log");
            int result = ValidationProcess.Run(executable, arguments, root, log, 900);
            Assert.True(result == 0, File.ReadAllText(log));
        }
        if (entry == "native")
            foreach (string architecture in new[] { "x86", "x64" })
                Run(Path.Combine(root, $"tests/FxDbg.IntegrationTests/bin/{configuration}/net48/FxDbg.IntegrationTests.{architecture}.exe"), new[] { root, configuration, suite }, "-" + architecture);
        else if (entry == "mcp")
            Run("dotnet", new[] { Path.Combine(root, $"tests/FxDbg.McpIntegrationTests/bin/{configuration}/net10.0-windows/FxDbg.McpIntegrationTests.dll"), Path.Combine(root, $"artifacts/mcp/{configuration}"), suite, root, configuration });
        else if (entry == "protocol")
            Run(Required("FXDBG_COVERAGE_NODE"), new[] { Path.Combine(root, "eng/test-mcp-protocol.mjs"), Path.Combine(root, $"artifacts/mcp/{configuration}") });
        else
            Run(Required("FXDBG_COVERAGE_NODE"), new[] { Path.Combine(root, "tests/FxDbg.DapTests/" + suite + ".js"), Path.Combine(root, $"artifacts/dap/{configuration}"), root, configuration });
    }
}
