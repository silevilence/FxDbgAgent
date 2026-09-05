using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

internal static class ResourceSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (string architecture in new[] { "x86", "x64" })
        {
            await using var connection = await McpTestConnection.Create(bundle, deadline.Token, arguments: ["--max-sessions", "2", "--max-calls", "1", "--max-control-calls", "2"]);
            var targets = new List<Process>();
            string exe = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            async Task<JsonNode> Call(string method, Dictionary<string, object?> args, string? error = null, CancellationToken? token = null)
                => await ObservationSuite.Call(connection.Client, method, args, token ?? deadline.Token, error);
            async Task<string> Launch()
            {
                var launched = await Call("launch", new() { ["exe"] = exe, ["args"] = new[] { "--wait-milliseconds", "120000" }, ["stopAtEntry"] = true });
                targets.Add(Process.GetProcessById(launched["result"]!["processId"]!.GetValue<int>()));
                return launched["sessionId"]!.GetValue<string>();
            }
            async Task<JsonNode> Poll(string session, Func<JsonNode, bool> matches, string? operation = null)
            {
                while (true)
                {
                    var args = new Dictionary<string, object?> { ["sessionId"] = session };
                    if (operation is not null) args["operationId"] = operation;
                    var state = (await Call("status", args))["result"]!;
                    if (matches(state)) return state;
                    await Task.Delay(10, deadline.Token);
                }
            }
            try
            {
                await Call("launch", new() { ["exe"] = Path.Combine(root, "missing-target.exe") }, "target_not_found");
                string first = await Launch(), second = await Launch();
                var limit = await Call("launch", new() { ["exe"] = exe }, "rate_limited");
                ObservationSuite.Require((int?)limit["error"]?["retryAfterMs"] == 1000, "Session saturation has retry guidance.");

                var waiting = Call("continue", new() { ["sessionId"] = first });
                await Poll(first, state => (string?)state["target"]?["sessionState"] == "running");
                await Call("threads", new() { ["sessionId"] = second }, "rate_limited");
                await Call("status", new() { ["sessionId"] = second });
                await Call("pause", new() { ["sessionId"] = first });
                ObservationSuite.Require((string?)(await waiting)["result"]?["state"] == "completed", "Reserved control completes saturated wait.");

                var timed = await Call("continue", new() { ["sessionId"] = first, ["timeoutMs"] = 150 }, "operation_timed_out");
                ObservationSuite.Require((string?)timed["result"]?["state"] == "timedOut" && (string?)timed["result"]?["stop"]?["reason"] == "userPause", "Synchronous deadline really pauses.");
                var accepted = await Call("continue", new() { ["sessionId"] = first, ["waitForStop"] = false, ["timeoutMs"] = 150 });
                string op = accepted["result"]!["operationId"]!.GetValue<string>();
                var terminal = await Poll(first, state => (string?)state["operation"]?["state"] != "running", op);
                ObservationSuite.Require((string?)terminal["operation"]?["state"] == "timedOut" && (string?)terminal["target"]?["sessionState"] == "stopped", "Asynchronous deadline survives initial response.");

                // A cancelled status is observation-only and must not cancel the active run.
                accepted = await Call("continue", new() { ["sessionId"] = first, ["waitForStop"] = false });
                op = accepted["result"]!["operationId"]!.GetValue<string>();
                Task<JsonRpcResponse> query = connection.Client.SendRequestAsync(new JsonRpcRequest { Id = new RequestId("status-race"), Method = "tools/call",
                    Params = new JsonObject { ["name"] = "debug_status", ["arguments"] = new JsonObject { ["sessionId"] = first } } }, deadline.Token);
                await connection.Client.SendMessageAsync(new JsonRpcNotification { Method = "notifications/cancelled", Params = new JsonObject { ["requestId"] = "status-race" } }, deadline.Token);
                try { await query; } catch (OperationCanceledException) { }
                terminal = await Poll(first, state => state["operation"] is not null, op);
                ObservationSuite.Require((string?)terminal["operation"]?["state"] == "running", "Status cancellation cannot cancel execution.");
                await Call("pause", new() { ["sessionId"] = first });
                await Call("terminate", new() { ["sessionId"] = first });

                var external = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-milliseconds", "120000" } })!;
                targets.Add(external);
                await Task.Delay(200, deadline.Token);
                string attached = (await Call("attach", new() { ["pid"] = external.Id }))["sessionId"]!.GetValue<string>();
                await Call("pause", new() { ["sessionId"] = attached });
                var requestId = new RequestId("resource-cancel-" + architecture);
                var pending = connection.Client.SendRequestAsync(new JsonRpcRequest { Id = requestId, Method = "tools/call",
                    Params = new JsonObject { ["name"] = "debug_continue", ["arguments"] = new JsonObject { ["sessionId"] = attached } } }, deadline.Token);
                var running = await Poll(attached, state => (string?)state["target"]?["sessionState"] == "running" && state["activeOperationId"] is not null);
                op = running["activeOperationId"]!.GetValue<string>();
                await connection.Client.SendMessageAsync(new JsonRpcNotification { Method = "notifications/cancelled",
                    Params = new JsonObject { ["requestId"] = "resource-cancel-" + architecture } }, deadline.Token);
                try { await pending; } catch (OperationCanceledException) { }
                terminal = await Poll(attached, state => (string?)state["operation"]?["state"] != "running", op);
                ObservationSuite.Require((string?)terminal["operation"]?["state"] == "cancelled", "Waiting cancellation reaches one cancelled terminal: " + terminal);
                ObservationSuite.Require(!external.HasExited, "Attached target survives request cancellation.");
                foreach (string cancelledId in new[] { "resource-cancel-" + architecture, "unknown-request" })
                    await connection.Client.SendMessageAsync(new JsonRpcNotification { Method = "notifications/cancelled", Params = new JsonObject { ["requestId"] = cancelledId } }, deadline.Token);
                ObservationSuite.Require((string?)(await Call("status", new() { ["sessionId"] = second }))["result"]?["target"]?["sessionState"] == "stopped", "Another session is not cancelled.");
                string reattached = (await Call("attach", new() { ["pid"] = external.Id }))["sessionId"]!.GetValue<string>();
                await Call("terminate", new() { ["sessionId"] = reattached }, "invalid_request");
                await Call("detach", new() { ["sessionId"] = reattached });
                await Call("terminate", new() { ["sessionId"] = second });
                Console.WriteLine($"MCP resources: {architecture}, saturation/reserved control, sync/async deadlines, status cancellation, attached wait cancellation and reattach passed.");
            }
            finally
            {
                foreach (Process target in targets)
                {
                    if (!target.HasExited) { target.Kill(); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await target.WaitForExitAsync(cleanup.Token); }
                    target.Dispose();
                }
            }
        }
    }
}
