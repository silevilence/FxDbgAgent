using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class LifecycleSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        foreach (string architecture in new[] { "x86", "x64" })
        foreach (bool attach in new[] { false, true })
        foreach (string mode in new[] { "eof-running", "eof-stopped", "eof-waiting", "kill-running", "kill-stopped", "kill-waiting", "output-waiting", "engine-crash" })
        {
            var connection = await McpTestConnection.Create(bundle, deadline.Token);
            Process? target = null;
            var engines = new List<Process>();
            string executable = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            Task<JsonNode>? waiting = null;
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args) => await ObservationSuite.Call(connection.Client, name, args, deadline.Token);
            try
            {
                JsonNode created;
                if (attach)
                {
                    target = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "120000" } })!;
                    await Task.Delay(150, deadline.Token);
                    created = await Call("attach", new() { ["pid"] = target.Id });
                    await Call("pause", new() { ["sessionId"] = (string)created["sessionId"]! });
                }
                else
                {
                    created = await Call("launch", new() { ["exe"] = executable, ["args"] = new[] { "--wait-milliseconds", "120000" }, ["stopAtEntry"] = true });
                    target = Process.GetProcessById(created["result"]!["processId"]!.GetValue<int>());
                }
                string session = created["sessionId"]!.GetValue<string>();
                engines.AddRange(ProcessInventory.Children(connection.ProcessId).Where(x => x.Name.StartsWith("FxDbg.Engine.")).Select(x => Process.GetProcessById(x.Id)));
                ObservationSuite.Require(engines.Count == 1, "One owned Engine must exist.");
                if (mode.Contains("running") || mode.Contains("waiting"))
                {
                    if (mode.Contains("waiting")) waiting = Call("continue", new() { ["sessionId"] = session, ["timeoutMs"] = 240000 });
                    else await Call("continue", new() { ["sessionId"] = session, ["waitForStop"] = false, ["timeoutMs"] = 240000 });
                    while ((string?)(await Call("status", new() { ["sessionId"] = session }))["result"]?["target"]?["sessionState"] != "running") await Task.Delay(10, deadline.Token);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var elapsed = Stopwatch.StartNew();
                if (mode.StartsWith("kill")) connection.KillHost();
                else if (mode.StartsWith("output"))
                {
                    connection.BreakOutput();
                    _ = Call("status", new() { ["sessionId"] = session }).ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
                else if (mode == "engine-crash") engines[0].Kill();
                else connection.CloseInput();
                foreach (Process engine in engines) await engine.WaitForExitAsync(cleanup.Token);
                if (mode == "engine-crash")
                {
                    JsonNode state;
                    do { state = (await Call("status", new() { ["sessionId"] = session }))["result"]!; }
                    while ((string?)state["target"]?["sessionState"] != "failed");
                    ObservationSuite.Require((string?)state["failureCode"] is "engine_exited" or "transport_disconnected", "Host isolates Engine failure.");
                    Console.WriteLine($"MCP lifecycle: {architecture} {(attach ? "attach" : "launch")} {mode}, targetExited={target.HasExited} (known CLR hard-crash limit).");
                    var replacement = await Call("launch", new() { ["exe"] = executable, ["stopAtEntry"] = true });
                    using var freshTarget = Process.GetProcessById(replacement["result"]!["processId"]!.GetValue<int>());
                    try { await Call("terminate", new() { ["sessionId"] = (string?)replacement["sessionId"] }); }
                    finally { if (!freshTarget.HasExited) freshTarget.Kill(); }
                }
                else
                {
                    await connection.WaitExit(cleanup.Token);
                    ObservationSuite.Require(!target.HasExited, "Normal EOF/Host failure must safely detach target.");
                    await using var reconnect = await McpTestConnection.Create(bundle, deadline.Token);
                    var reattached = await ObservationSuite.Call(reconnect.Client, "attach", new() { ["pid"] = target.Id }, deadline.Token);
                    await ObservationSuite.Call(reconnect.Client, "detach", new() { ["sessionId"] = (string?)reattached["sessionId"] }, deadline.Token);
                    ObservationSuite.Require(!target.HasExited, "Reattach/detach preserves target.");
                    Console.WriteLine($"MCP lifecycle: {architecture} {(attach ? "attach" : "launch")} {mode}, Engines exited in {elapsed.ElapsedMilliseconds} ms, target alive and reattached.");
                }
            }
            finally
            {
                if (target is not null) { if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(deadline.Token); } target.Dispose(); }
                await connection.DisposeAsync();
                if (waiting is not null) try { await waiting; } catch (Exception) { }
                foreach (Process engine in engines) engine.Dispose();
            }
        }
        await LifecycleConcurrencySuite.Run(bundle, root, configuration);
    }
}
