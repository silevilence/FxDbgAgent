using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

// VSTest's built-in Microsoft collector follows child Host/Engine processes.
// This opt-in harness measures the existing real matrix, without duplicating it.
public sealed class RealMatrixCoverage
{
    [Fact]
    public async Task Published_service_or_iis_matrix_passes_under_coverage()
    {
        string Required(string name)=>Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Use eng/collect-stage3-4-coverage.ps1; missing "+name);
        string root=Path.GetFullPath(Required("FXDBG_COVERAGE_ROOT")),configuration=Required("FXDBG_COVERAGE_CONFIGURATION"),suite=Required("FXDBG_COVERAGE_SUITE");
        Assert.Contains(configuration,new[]{"Debug","Release"});
        Assert.Contains(suite,new[]{"permissions","iis"});
        Assert.True(File.Exists(Path.Combine(root,"FxDbg.sln")));
        string manifest=Path.Combine(root,$"artifacts/stage3-4-environment-{suite}/state.json");
        Assert.True(File.Exists(manifest));
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root };
        foreach(string argument in new[]{Path.Combine(root,$"tests/FxDbg.McpIntegrationTests/bin/{configuration}/net10.0-windows/FxDbg.McpIntegrationTests.dll"),
            Path.Combine(root,$"artifacts/mcp/{configuration}"),suite,root,configuration,manifest}) start.ArgumentList.Add(argument);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var process=Process.Start(start)!;
        Task<string> output=process.StandardOutput.ReadToEndAsync(),error=process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if(!process.HasExited) { process.Kill();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        string[] logs=await Task.WhenAll(output,error).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode==0,string.Join(Environment.NewLine,logs));
    }
}
