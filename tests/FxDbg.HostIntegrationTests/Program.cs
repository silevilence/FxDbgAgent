using System.Diagnostics;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;
using Newtonsoft.Json.Linq;

string root = Path.GetFullPath(args[0]);
string configuration = args[1];
if (args.Length > 2 && args[2] == "e2e")
{
    await EndToEndSuite.Run(root, configuration);
    return;
}
EngineProcessHost CreateHost()
{
    string engineRoot = Path.Combine(root, "src", "FxDbg.Engine", "bin", configuration, "net48");
    return new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
        new EngineProcessPaths(Path.Combine(engineRoot, "FxDbg.Engine.x86.exe"), Path.Combine(engineRoot, "FxDbg.Engine.x64.exe")));
}
string Target(string architecture, bool variables = false)
{
    string name = variables ? architecture == "x86" ? "Fx40.ModuleLifecycle.x86" : "Fx40.ModuleLifecycle" : "Fx40.Console." + architecture;
    return Path.Combine(root, "tests", "Debuggees", name, "bin", configuration, "net40", name + ".exe");
}
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task WaitExit(Process process)
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    await process.WaitForExitAsync(limit.Token);
}

if (args.Length > 2 && args[2] == "crash-helper")
{
    using var helperHost = CreateHost();
    var helperId = SessionId.New();
    await helperHost.AttachAsync(new AttachRequest(helperId, int.Parse(args[3]), TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
    await helperHost.InvokeAsync(helperId, "pause");
    Console.WriteLine(helperHost.GetEngineProcessId(helperId));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

foreach (string architecture in new[] { "x86", "x64" })
{
    using (var host = CreateHost())
    {
        var id = SessionId.New();
        var target = await host.LaunchAsync(new LaunchRequest(id, Target(architecture, true), new[] { "--variables" }, null, null,
            TargetArchitecture.Auto, true, TimeSpan.FromSeconds(15)), CancellationToken.None);
        using Process process = Process.GetProcessById(target.ProcessId);
        try
        {
            string source = Path.Combine(root, "tests", "Debuggees", "Fx40.ModuleLifecycle", "Program.cs");
            int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(item => item.text.Contains("// VARIABLE_BREAKPOINT")).index + 1;
            JToken breakpoint = await host.InvokeAsync(id, "break.set", new JObject { ["file"] = source, ["line"] = line });
            await host.InvokeAsync(id, "continue");
            JToken stop = await host.InvokeAsync(id, "wait", timeout: TimeSpan.FromSeconds(15));
            Require((string?)stop["reason"] == "breakpoint", "Cross-process source breakpoint must stop.");
            JToken stack = await host.InvokeAsync(id, "stack", new JObject { ["threadId"] = stop["threadId"]!.DeepClone() });
            string frame = (string)stack[0]!["frameId"]!;
            JToken variables = await host.InvokeAsync(id, "variables", new JObject { ["frameId"] = frame, ["maxDepth"] = 0 });
            Require(variables.Any(value => (string?)value["name"] == "number" && (string?)value["displayValue"] == "42"), "Wire variables must use the product reader.");
            Require((await host.InvokeAsync(id, "modules")).Any(module => (string?)module["symbolStatus"] == "loaded"), "Module symbol states must cross the wire.");
            await host.InvokeAsync(id, "break.remove", new JObject { ["breakpointId"] = breakpoint["breakpointId"]!.DeepClone() });
            var events = host.DrainEvents(id);
            Require(events.Any(item => (string?)item["kind"] == "stopped"), "Stopped event must arrive independently of its response.");
            Require(events.Select(item => (long)item["sequence"]!).SequenceEqual(Enumerable.Range(1, events.Count).Select(value => (long)value)), "Wire events must be ordered.");
            await host.InvokeAsync(id, "detach");
            await WaitExit(process);
            Require(!host.ActiveSessions.Contains(id), "Detach must remove the Host session.");
        }
        finally { if (!process.HasExited) { process.Kill(); await WaitExit(process); } }
    }
    Console.WriteLine("PASS: " + architecture + " cross-process commands and events.");

    using (var host = CreateHost())
    using (var target = Process.Start(new ProcessStartInfo(Target(architecture)) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "60000" } })!)
    {
        try
        {
            await Task.Delay(500);
            var id = SessionId.New();
            await host.AttachAsync(new AttachRequest(id, target.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(15)), CancellationToken.None);
            using (Process engine = Process.GetProcessById(host.GetEngineProcessId(id))) { engine.Kill(); await WaitExit(engine); }
            try { await host.InvokeAsync(id, "state"); throw new InvalidOperationException("Crashed Engine request unexpectedly succeeded."); }
            catch (FxDbgException error) { Require(error.Code is FxDbgErrorCode.EngineExited or FxDbgErrorCode.TransportDisconnected, "Engine crash must report a transport/process failure."); }
            Require(!host.ActiveSessions.Contains(id), "Engine crash must not leave an active Host session.");
            await Task.Delay(500);
            target.Refresh();
            Console.WriteLine("OBSERVED: forced Engine crash, target exited=" + target.HasExited + (target.HasExited ? ", exitCode=" + target.ExitCode : ""));
            using var freshTarget = Process.Start(new ProcessStartInfo(Target(architecture)) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "30000" } })!;
            var replacement = SessionId.New();
            try
            {
                await Task.Delay(500);
                await host.AttachAsync(new AttachRequest(replacement, freshTarget.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
                await host.InvokeAsync(replacement, "detach");
                Require(!freshTarget.HasExited, "Normal detach must preserve attached target.");
            }
            finally { if (!freshTarget.HasExited) { freshTarget.Kill(); await WaitExit(freshTarget); } }
        }
        finally { if (!target.HasExited) { target.Kill(); await WaitExit(target); } }
    }
    Console.WriteLine("PASS: " + architecture + " Engine crash isolation and Host reuse.");

    using (var host = CreateHost())
    using (var target = Process.Start(new ProcessStartInfo(Target(architecture)) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "60000" } })!)
    {
        try
        {
            await Task.Delay(500);
            var id = SessionId.New();
            await host.AttachAsync(new AttachRequest(id, target.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
            try { await host.InvokeAsync(id, "wait", timeout: TimeSpan.FromMilliseconds(150)); throw new InvalidOperationException("Wait should time out."); }
            catch (FxDbgException error) { Require(error.Code == FxDbgErrorCode.OperationTimedOut, "Remote command deadline must remain a timeout."); }
            Require((string?)(await host.InvokeAsync(id, "state"))["target"]?["sessionState"] == "stopped", "Cooperative timeout must leave the target inspectable.");
            await host.InvokeAsync(id, "continue");
            using Process engine = Process.GetProcessById(host.GetEngineProcessId(id));
            using var cancellation = new CancellationTokenSource(150);
            try { await host.InvokeAsync(id, "wait", timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellation.Token); throw new InvalidOperationException("Wait should cancel."); }
            catch (FxDbgException error) { Require(error.Code == FxDbgErrorCode.OperationCancelled, "Client cancellation must remain explicit."); }
            await WaitExit(engine);
            Require(!target.HasExited && !host.ActiveSessions.Contains(id), "Client abort must close the uncertain session and safely detach.");
            using Process self = Process.GetCurrentProcess();
            self.Refresh();
            int before = self.HandleCount;
            for (int cycle = 0; cycle < 5; cycle++)
            {
                var next = SessionId.New();
                await host.AttachAsync(new AttachRequest(next, target.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
                using Process ownedEngine = Process.GetProcessById(host.GetEngineProcessId(next));
                await host.InvokeAsync(next, "detach");
                await WaitExit(ownedEngine);
            }
            GC.Collect(); GC.WaitForPendingFinalizers();
            self.Refresh();
            Require(self.HandleCount - before < 32 && host.ActiveSessions.Count == 0, "Repeated sessions must not accumulate Host handles or active Engines.");
        }
        finally { if (!target.HasExited) { target.Kill(); await WaitExit(target); } }
    }
    Console.WriteLine("PASS: " + architecture + " timeout, cancellation and repeated cleanup.");

    using (var target = Process.Start(new ProcessStartInfo(Target(architecture)) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "60000" } })!)
    {
        Process? child = null;
        try
        {
            await Task.Delay(500);
            child = Process.Start(new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { typeof(Program).Assembly.Location, root, configuration, "crash-helper", target.Id.ToString() }
            })!;
            using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string? ready = await child.StandardOutput.ReadLineAsync(readyTimeout.Token);
            if (!int.TryParse(ready, out int engineId)) throw new InvalidOperationException("Crash helper failed: " + await child.StandardError.ReadToEndAsync());
            using Process engine = Process.GetProcessById(engineId);
            child.Kill();
            await WaitExit(child);
            await WaitExit(engine);
            Require(!target.HasExited, "Host crash must preserve the paused attached target.");
            using var reattach = CreateHost();
            var id = SessionId.New();
            await reattach.AttachAsync(new AttachRequest(id, target.Id, TargetArchitecture.Auto, TimeSpan.FromSeconds(10)), CancellationToken.None);
            await reattach.InvokeAsync(id, "detach");
        }
        finally
        {
            if (child is not null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
            if (!target.HasExited) { target.Kill(); await WaitExit(target); }
        }
    }
    Console.WriteLine("PASS: " + architecture + " Host crash safely detaches and exits Engine.");
}
