using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class LifecycleConcurrencySuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string Target(string architecture) => Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
        Process StartTarget(string architecture) => Process.Start(new ProcessStartInfo(Target(architecture)) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "120000" } })!;
        async Task KillOwned(Process process)
        {
            if (!process.HasExited) { process.Kill(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await process.WaitForExitAsync(stop.Token); }
            process.Dispose();
        }
        foreach (bool kill in new[] { false, true })
        {
            var connection = await McpTestConnection.Create(bundle, deadline.Token);
            var targets = new List<Process>();
            var waits = new List<Task<JsonNode>>();
            var engines = new List<Process>();
            try
            {
                for (int index = 0; index < 8; index++)
                {
                    Process target = StartTarget(index % 2 == 0 ? "x86" : "x64"); targets.Add(target);
                    await Task.Delay(100, deadline.Token);
                    var attached = await ObservationSuite.Call(connection.Client, "attach", new() { ["pid"] = target.Id }, deadline.Token);
                    string session = attached["sessionId"]!.GetValue<string>();
                    await ObservationSuite.Call(connection.Client, "pause", new() { ["sessionId"] = session }, deadline.Token);
                    if (index % 2 == 0) waits.Add(ObservationSuite.Call(connection.Client, "continue", new() { ["sessionId"] = session, ["timeoutMs"] = 240000 }, deadline.Token));
                }
                engines.AddRange(ProcessInventory.Children(connection.ProcessId).Where(x => x.Name.StartsWith("FxDbg.Engine.")).Select(x => Process.GetProcessById(x.Id)));
                ObservationSuite.Require(engines.Count == 8, "Eight independent Engines at capacity.");
                var elapsed = Stopwatch.StartNew();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (kill) connection.KillHost(); else { connection.CloseInput(); connection.CloseInput(); }
                await Task.WhenAll(engines.Select(x => x.WaitForExitAsync(cleanup.Token)));
                await connection.WaitExit(cleanup.Token);
                ObservationSuite.Require(targets.All(x => !x.HasExited), "All attached targets survive group cleanup.");
                await using var reconnect = await McpTestConnection.Create(bundle, deadline.Token);
                foreach (Process target in targets)
                {
                    var attached = await ObservationSuite.Call(reconnect.Client, "attach", new() { ["pid"] = target.Id }, deadline.Token);
                    await ObservationSuite.Call(reconnect.Client, "detach", new() { ["sessionId"] = (string?)attached["sessionId"] }, deadline.Token);
                }
                Console.WriteLine($"MCP lifecycle: eight mixed-architecture sessions, {(kill ? "Host kill" : "repeated EOF")}, all Engines exited and targets reattached in {elapsed.ElapsedMilliseconds} ms.");
            }
            finally
            {
                foreach (Process target in targets) await KillOwned(target);
                await connection.DisposeAsync(); await connection.DisposeAsync();
                foreach (Task<JsonNode> waiting in waits) try { await waiting; } catch (Exception) { }
                foreach (Process engine in engines) engine.Dispose();
            }
        }

        foreach (string architecture in new[] { "x86", "x64" })
        foreach (bool attach in new[] { false, true })
        {
            var connection = await McpTestConnection.Create(bundle, deadline.Token);
            Process? target = attach ? StartTarget(architecture) : null;
            var engines = new List<Process>();
            try
            {
                if (attach) await Task.Delay(100, deadline.Token);
                Task<JsonNode> pending = ObservationSuite.Call(connection.Client, attach ? "attach" : "launch",
                    attach ? new() { ["pid"] = target!.Id, ["timeoutMs"] = 240000 } : new() { ["exe"] = Target(architecture), ["args"] = new[] { "--wait-milliseconds", "120000" }, ["timeoutMs"] = 240000 }, deadline.Token);
                while (engines.Count == 0)
                {
                    engines.AddRange(ProcessInventory.Children(connection.ProcessId).Where(x => x.Name.StartsWith("FxDbg.Engine.")).Select(x => Process.GetProcessById(x.Id)));
                    if (engines.Count == 0) await Task.Delay(1, deadline.Token);
                }
                ObservationSuite.Require(!pending.IsCompleted, "Interrupt must occur before startup response.");
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                connection.CloseInput();
                await Task.WhenAll(engines.Select(x => x.WaitForExitAsync(cleanup.Token)));
                await connection.WaitExit(cleanup.Token);
                try { await pending; } catch (Exception) { }
                if (!attach)
                {
                    var children = engines.SelectMany(x => ProcessInventory.Children(x.Id)).Where(x => x.Name.StartsWith("Fx40.Console.")).ToArray();
                    if (children.Length > 0) target = Process.GetProcessById(children.Single().Id);
                }
                if (target is not null)
                {
                    ObservationSuite.Require(!target.HasExited, "A created target survives interrupted startup.");
                    await using var reconnect = await McpTestConnection.Create(bundle, deadline.Token);
                    var attached = await ObservationSuite.Call(reconnect.Client, "attach", new() { ["pid"] = target.Id }, deadline.Token);
                    await ObservationSuite.Call(reconnect.Client, "detach", new() { ["sessionId"] = (string?)attached["sessionId"] }, deadline.Token);
                }
                Console.WriteLine($"MCP lifecycle: {architecture} {(attach ? "attach" : "launch")} interrupted before startup response, Engine exited; target {(target is null ? "not created" : "alive and reattached")}.");
            }
            finally
            {
                if (target is not null) await KillOwned(target);
                await connection.DisposeAsync();
                foreach (Process engine in engines) engine.Dispose();
            }
        }

        await using var reused = await McpTestConnection.Create(bundle, deadline.Token);
        using Process host = Process.GetProcessById(reused.ProcessId);
        int baseline = 0;
        for (int index = 0; index < 12; index++)
        {
            var launched = await ObservationSuite.Call(reused.Client, "launch", new() { ["exe"] = Target(index % 2 == 0 ? "x86" : "x64"), ["stopAtEntry"] = true }, deadline.Token);
            using Process target = Process.GetProcessById(launched["result"]!["processId"]!.GetValue<int>());
            try { await ObservationSuite.Call(reused.Client, "terminate", new() { ["sessionId"] = (string?)launched["sessionId"] }, deadline.Token); }
            finally { if (!target.HasExited) target.Kill(); }
            ObservationSuite.Require(ProcessInventory.Children(reused.ProcessId).All(x => !x.Name.StartsWith("FxDbg.Engine.")), "Ended sessions release Engines immediately.");
            host.Refresh();
            if (index == 1) baseline = host.HandleCount;
        }
        host.Refresh();
        ObservationSuite.Require(host.HandleCount <= baseline + 32, $"Repeated sessions grow handles: baseline={baseline}, final={host.HandleCount}");
        Console.WriteLine($"MCP lifecycle: twelve launch/terminate cycles, Host handles baseline={baseline}, final={host.HandleCount}, no owned Engine remains.");
    }
}
