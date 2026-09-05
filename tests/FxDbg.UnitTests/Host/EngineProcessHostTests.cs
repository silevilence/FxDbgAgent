using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class EngineProcessHostTests
{
    [Fact]
    public async Task Launch_auto_routes_each_PE_to_the_matching_engine()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = FindRepositoryRoot();
        string engineRoot = Path.Combine(root, "src", "FxDbg.Engine", "bin", configuration, "net48");
        using var host = new EngineProcessHost(
            new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
            new EngineProcessPaths(
                Path.Combine(engineRoot, "FxDbg.Engine.x86.exe"),
                Path.Combine(engineRoot, "FxDbg.Engine.x64.exe")));

        foreach ((string project, TargetArchitecture expected) in new[]
        {
            ("Fx40.Console.x86", TargetArchitecture.X86),
            ("Fx40.Console.x64", TargetArchitecture.X64)
        })
        {
            string executable = Path.Combine(root, "tests", "Debuggees", project, "bin", configuration, "net40", project + ".exe");
            var request = new LaunchRequest(
                SessionId.New(),
                executable,
                new List<string> { "--probe" },
                null,
                null,
                TargetArchitecture.Auto,
                false,
                TimeSpan.FromSeconds(10));

            DebugTargetInfo target = await host.LaunchAsync(request, CancellationToken.None);

            Assert.Equal(expected, target.Architecture);
            Assert.Matches(@"^v4\.0\.\d+$", target.RuntimeVersion);
            Assert.False(string.IsNullOrWhiteSpace(target.RuntimeFileVersion));
            Assert.True(target.LaunchedByDebugger);
            Assert.Equal(DebugSessionState.Running, target.SessionState);
        }
    }

    [Fact]
    public async Task Launch_forwards_stop_at_entry_and_returns_stopped_state()
    {
        (EngineProcessHost host, string root, string configuration) = CreateHost();
        using (host)
        {
            string executable = Path.Combine(
                root,
                "tests",
                "Debuggees",
                "Fx40.Console.x86",
                "bin",
                configuration,
                "net40",
                "Fx40.Console.x86.exe");
            string observation = Path.Combine(Path.GetTempPath(), "fxdbg-stop-at-entry-" + Guid.NewGuid().ToString("N") + ".txt");
            var request = new LaunchRequest(
                SessionId.New(),
                executable,
                new List<string> { "--launch-observation", observation, "must-not-run-before-return" },
                null,
                null,
                TargetArchitecture.Auto,
                true,
                TimeSpan.FromSeconds(10));

            DebugTargetInfo target = await host.LaunchAsync(request, CancellationToken.None);

            Assert.Equal(DebugSessionState.Stopped, target.SessionState);
            Assert.False(File.Exists(observation));

            host.Dispose();
            Assert.True(SpinWait.SpinUntil(
                () => ObservationCompleted(observation, "must-not-run-before-return"),
                TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(() => TryDelete(observation), TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Attach_auto_routes_x86_and_x64_targets()
    {
        (EngineProcessHost host, string root, string configuration) = CreateHost();
        using (host)
        {
            foreach ((string project, TargetArchitecture expected) in new[]
            {
                ("Fx40.Console.x86", TargetArchitecture.X86),
                ("Fx40.Console.x64", TargetArchitecture.X64)
            })
            {
                string executable = Path.Combine(root, "tests", "Debuggees", project, "bin", configuration, "net40", project + ".exe");
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "--wait-milliseconds", "30000" }
                })!;
                try
                {
                    await Task.Delay(700);
                    var request = new AttachRequest(SessionId.New(), process.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(10));

                    DebugTargetInfo target = await host.AttachAsync(request, CancellationToken.None);

                    Assert.Equal(expected, target.Architecture);
                    Assert.Equal(DebugSessionState.Running, target.SessionState);
                    Assert.False(process.HasExited);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
            }
        }
    }

    private static (EngineProcessHost Host, string Root, string Configuration) CreateHost()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = FindRepositoryRoot();
        string engineRoot = Path.Combine(root, "src", "FxDbg.Engine", "bin", configuration, "net48");
        var host = new EngineProcessHost(
            new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
            new EngineProcessPaths(
                Path.Combine(engineRoot, "FxDbg.Engine.x86.exe"),
                Path.Combine(engineRoot, "FxDbg.Engine.x64.exe")));
        return (host, root, configuration);
    }

    private static bool ObservationCompleted(string path, string expectedSuffix)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).EndsWith(expectedSuffix, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FxDbg.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate FxDbg.sln.");
    }
}
