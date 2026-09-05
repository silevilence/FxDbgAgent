using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

internal static class ExecutionSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var connection = await McpTestConnection.Create(bundle, deadline.Token);
        McpClient client = connection.Client;
        var targets = new List<Process>();
        string source = Path.Combine(root, "tests/Debuggees/Shared/EndToEndScenarios.cs");
        int Line(string marker) => File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains("// " + marker)).index + 1;
        async Task<JsonNode> Call(string method, string session, Dictionary<string, object?>? args = null, string? error = null)
        {
            args ??= new(); args["sessionId"] = session;
            return await ObservationSuite.Call(client, method, args, deadline.Token, error);
        }
        async Task<string> Launch(string architecture, string[] args)
        {
            string executable = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            var result = await ObservationSuite.Call(client, "launch", new() { ["exe"] = executable, ["args"] = args, ["stopAtEntry"] = true }, deadline.Token);
            targets.Add(Process.GetProcessById(result["result"]!["processId"]!.GetValue<int>()));
            return result["sessionId"]!.GetValue<string>();
        }
        try
        {
            foreach (string architecture in new[] { "x86", "x64" })
            {
                string directory = Path.Combine(root, "artifacts/mcp-execution", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string session = await Launch(architecture, ["--scenario", "values", directory]);
                var breakpoint = (await Call("set_breakpoint", session, new() { ["file"] = source, ["line"] = Line("E2E_BREAKPOINT") }))["result"]!;
                var continued = (await Call("continue", session))["result"]!;
                ObservationSuite.Require((string?)continued["state"] == "completed" && (string?)continued["stop"]?["reason"] == "breakpoint", "Waiting continue gets the new breakpoint.");
                string firstOperation = continued["operationId"]!.GetValue<string>();
                int thread = continued["stop"]!["threadId"]!.GetValue<int>();
                var stack = (await Call("stack", session, new() { ["threadId"] = thread }))["result"]!.AsArray();
                string oldFrame = stack[0]!["frameId"]!.GetValue<string>();
                foreach (string kind in new[] { "over", "into", "out" })
                {
                    var stepped = (await Call("step", session, new() { ["threadId"] = thread, ["kind"] = kind }))["result"]!;
                    ObservationSuite.Require((string?)stepped["stop"]?["reason"] == "step", "Step " + kind + " produces a new stop.");
                    if (kind == "into") ObservationSuite.Require(stepped["stop"]!["methodName"]!.GetValue<string>().Contains("AddOne"), "Step into enters AddOne.");
                    thread = stepped["stop"]!["threadId"]!.GetValue<int>();
                }
                await Call("variables", session, new() { ["frameId"] = oldFrame }, "frame_not_found");
                await Call("remove_breakpoint", session, new() { ["breakpointId"] = breakpoint["breakpointId"]!.GetValue<string>() });
                var accepted = (await Call("continue", session, new() { ["waitForStop"] = false }))["result"]!;
                string operation = accepted["operationId"]!.GetValue<string>();
                JsonNode completed;
                do
                {
                    completed = (await Call("status", session, new() { ["operationId"] = operation }))["result"]!["operation"]!;
                    if ((string?)completed["state"] == "running") await Task.Delay(10, deadline.Token);
                } while ((string?)completed["state"] == "running");
                ObservationSuite.Require((string?)completed["state"] == "completed" && (string?)completed["stop"]?["reason"] == "processExit", "Async run captures natural exit, including immediate stop before response: " + completed.ToJsonString());
                var repeated = (await Call("status", session, new() { ["operationId"] = operation }))["result"]!["operation"]!;
                ObservationSuite.Require(JsonNode.DeepEquals(completed, repeated), "Repeated polling is immutable.");
                ObservationSuite.Require((string?)(await Call("status", session, new() { ["operationId"] = firstOperation }))["result"]!["operation"]!["stop"]?["reason"] == "breakpoint", "Old operation keeps its own stop.");
                await Call("status", session, new() { ["operationId"] = Guid.NewGuid().ToString() }, "operation_not_found");

                string looping = await Launch(architecture, ["--wait-milliseconds", "120000"]);
                string other = await Launch(architecture, ["--wait-milliseconds", "120000"]);
                for (int index = 0; index < 100; index++)
                {
                    var running = (await Call("continue", looping, new() { ["waitForStop"] = false }))["result"]!;
                    string runningId = running["operationId"]!.GetValue<string>();
                    if (index == 0)
                    {
                        await Call("continue", looping, new() { ["waitForStop"] = false }, "invalid_session_state");
                        await Call("status", other, new() { ["operationId"] = runningId }, "operation_not_found");
                        var otherStatus = (await Call("status", other))["result"]!;
                        ObservationSuite.Require((string?)otherStatus["target"]?["sessionState"] == "stopped", "Other session is responsive during an active run.");
                    }
                    var pause = (await Call("pause", looping))["result"]!;
                    ObservationSuite.Require((string?)pause["reason"] == "userPause", "Pause stops target.");
                    var pausedOperation = (await Call("status", looping, new() { ["operationId"] = runningId }))["result"]!["operation"]!;
                    ObservationSuite.Require((string?)pausedOperation["state"] == "completed", "Pause completes exactly its active operation.");
                }
                var pendingWait = Call("continue", looping);
                while ((await Call("status", looping))["result"]!["activeOperationId"] is null) await Task.Delay(10, deadline.Token);
                await Call("status", other);
                await Call("pause", looping);
                ObservationSuite.Require((string?)(await pendingWait)["result"]!["stop"]?["reason"] == "userPause", "Waiting mode does not block pause/status.");
                await Call("terminate", looping);
                await Call("detach", other);
                ObservationSuite.Require((string?)(await Call("status", other))["result"]!["target"]?["sessionState"] == "terminated", "Detached session retains terminal status.");

                string exceptionSession = await Launch(architecture, ["--scenario", "exception", directory]);
                var exception = (await Call("continue", exceptionSession))["result"]!["stop"]!;
                ObservationSuite.Require((string?)exception["reason"] == "exception" && (string?)exception["exception"]?["message"] == "e2e-unhandled-message", "Unhandled exception uses the shared safe field reader.");
                await Call("terminate", exceptionSession);
                Console.WriteLine($"MCP execution: {architecture}, three steps, sync/async stops, exit, 100 continue/pause cycles, concurrent sessions and exception passed.");
            }
        }
        finally
        {
            foreach (Process target in targets)
            {
                if (!target.HasExited) { target.Kill(); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await target.WaitForExitAsync(cleanup.Token); }
                target.Dispose();
            }
            await connection.DisposeAsync();
        }
    }
}
