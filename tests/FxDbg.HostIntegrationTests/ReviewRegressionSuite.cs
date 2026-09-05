using System.Diagnostics;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;

internal static class ReviewRegressionSuite
{
    internal static async Task Run(string root, string configuration)
    {
        foreach (string architecture in new[] { "x86", "x64" })
        {
            string engineRoot = Path.Combine(root, "src", "FxDbg.Engine", "bin", configuration, "net48");
            EngineProcessHost Host() => new(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
                new EngineProcessPaths(Path.Combine(engineRoot, "FxDbg.Engine.x86.exe"), Path.Combine(engineRoot, "FxDbg.Engine.x64.exe")));
            string name = "Fx40.Console." + architecture;
            string targetPath = Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
            string directory = Path.Combine(root, "artifacts", "review-regressions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var first = Host();
            using var second = Host();
            var id = SessionId.New();
            var target = await first.LaunchAsync(new LaunchRequest(id, targetPath, new[] { "--scenario", "runtime", directory }, null, null,
                TargetArchitecture.Auto, false, TimeSpan.FromSeconds(15)), CancellationToken.None);
            using Process process = Process.GetProcessById(target.ProcessId);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                string versionFile = Path.Combine(directory, "runtime-file-version");
                while (!File.Exists(versionFile)) await Task.Delay(20, deadline.Token);
                string expected = File.ReadAllText(versionFile);
                string moniker = "v" + Version.Parse(File.ReadAllText(Path.Combine(directory, "runtime-version")).Substring(1)).ToString(3);
                Console.WriteLine("RUNTIME " + architecture + ": CLR=" + target.RuntimeVersion + ", file=" + target.RuntimeFileVersion + ", target file=" + expected);
                await first.InvokeAsync(id, "pause");
                try
                {
                    await second.AttachAsync(new AttachRequest(SessionId.New(), target.ProcessId, TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
                    throw new InvalidOperationException("A second debugger unexpectedly attached.");
                }
                catch (FxDbgException error)
                {
                    if (error.Code != FxDbgErrorCode.AlreadyDebugged)
                        throw new InvalidOperationException("Attach conflict must return already_debugged; got " + error.Code + ": " + error.Message, error);
                }
                if (target.RuntimeVersion != moniker || target.RuntimeFileVersion != expected) throw new InvalidOperationException("Launch must report the target CLR identity and file version.");
                await first.InvokeAsync(id, "continue");
                await first.InvokeAsync(id, "detach");
                if (process.HasExited) throw new InvalidOperationException("Attach conflict or Detach terminated the original target.");
                var attachedId = SessionId.New();
                var attached = await second.AttachAsync(new AttachRequest(attachedId, process.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
                if (attached.RuntimeVersion != moniker || attached.RuntimeFileVersion != expected) throw new InvalidOperationException("Attach must report the target CLR identity and file version.");
                await second.InvokeAsync(attachedId, "detach");
                if (process.HasExited) throw new InvalidOperationException("Final Detach terminated the attached target.");
                Console.WriteLine("PASS: " + architecture + " runtime version, attach conflict, original session reuse and reattach.");
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        }
    }
}
