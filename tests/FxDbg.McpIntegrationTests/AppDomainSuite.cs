using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class AppDomainSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        foreach (string architecture in new[] { "x86", "x64" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string directory = Path.Combine(root, "artifacts/stage3-validation", "domains-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string source = Path.Combine(root, "tests/Debuggees/Shared/AppDomainScenarios.cs");
            int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains("// DOMAIN_BREAKPOINT")).index + 1;
            string exe = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            await using var connection = await McpTestConnection.Create(bundle, timeout.Token);
            var created = await ObservationSuite.Call(connection.Client, "launch", new()
            { ["exe"] = exe, ["args"] = new[] { "--scenario", "domains", directory } }, timeout.Token);
            string session = (string)created["sessionId"]!;
            using var target = Process.GetProcessById((int)created["result"]!["processId"]!);
            _ = target.Handle;
            async Task<JsonNode> Call(string name, Dictionary<string, object?>? args = null, string? error = null)
            {
                args ??= new(); args["sessionId"] = session;
                return (await ObservationSuite.Call(connection.Client, name, args, timeout.Token, error))[error is null ? "result" : "error"]!;
            }
            async Task Ready(string label)
            {
                while (!File.Exists(Path.Combine(directory, "ready-" + label))) await Task.Delay(20, timeout.Token);
            }
            async Task<JsonNode> Stopped()
            {
                while (true)
                {
                    var status = await Call("status");
                    if ((string?)status["target"]!["sessionState"] == "stopped") return status;
                    await Task.Delay(20, timeout.Token);
                }
            }
            try
            {
                await Ready("a"); await Ready("b");
                var status = await Call("status");
                string Domain(JsonNode snapshot, string label) => (string)snapshot["appDomains"]!.AsArray()
                    .Single(x => (int)x!["runtimeId"]! == int.Parse(File.ReadAllText(Path.Combine(directory, "ready-" + label))))!["appDomainId"]!;
                string first = Domain(status, "a"), second = Domain(status, "b");
                ObservationSuite.Require(first != second && status["appDomains"]!.AsArray().Count(x => (string?)x!["name"] == "SameName") == 2, "Same names have distinct lifetime IDs: " + status["appDomains"]);
                var breakpoint = await Call("set_breakpoint", new() { ["file"] = source, ["line"] = line, ["appDomainId"] = first });
                string breakpointId = (string)breakpoint["breakpointId"]!;
                ObservationSuite.Require(breakpoint["boundAppDomainIds"]!.AsArray().Count == 1 && (string?)breakpoint["boundAppDomainIds"]![0] == first, "Only selected domain bound.");
                File.WriteAllText(Path.Combine(directory, "go-b"), "go");
                while (!File.Exists(Path.Combine(directory, "done-b"))) await Task.Delay(20, timeout.Token);
                var otherStatus = await Call("status");
                ObservationSuite.Require((string?)otherStatus["target"]!["sessionState"] == "running", "Other domain does not stop: " + otherStatus);
                File.WriteAllText(Path.Combine(directory, "go-a"), "go");
                status = await Stopped();
                ObservationSuite.Require((string?)status["stop"]!["appDomainId"] == first, "Stop has selected domain ID.");
                var domainStatus = await Call("status", new() { ["appDomainId"] = second });
                ObservationSuite.Require(domainStatus["appDomains"]!.AsArray().Count == 1 && domainStatus["modules"]!.AsArray().All(x => (string?)x!["appDomainId"] == second)
                    && (string?)domainStatus["stop"]!["appDomainId"] == first, "Filtered metadata preserves whole-process stop context.");
                int thread = (int)status["stop"]!["threadId"]!;
                var threads = (await Call("threads", new() { ["appDomainId"] = first })).AsArray();
                ObservationSuite.Require(threads.Any(x => (int)x!["threadId"]! == thread) && threads.All(x => (string?)x!["appDomainId"] == first), "Threads filtered by current domain.");
                var stack = (await Call("stack", new() { ["threadId"] = thread })).AsArray();
                ObservationSuite.Require(stack.Where(x => x!["appDomainId"] is not null).Select(x => (string?)x!["appDomainId"]).Distinct().Count() >= 2, "Cross-domain stack retains each frame's domain.");
                string frame = (string)stack[0]!["frameId"]!;
                var filtered = (await Call("stack", new() { ["threadId"] = thread, ["appDomainId"] = first })).AsArray();
                ObservationSuite.Require(filtered.All(x => (string?)x!["appDomainId"] == first) && (string?)filtered[0]!["frameId"] == frame, "Filtered frame IDs preserve physical frame identity.");
                var variables = (await Call("variables", new() { ["frameId"] = frame, ["appDomainId"] = first, ["maxDepth"] = 0 })).AsArray();
                ObservationSuite.Require(variables.All(x => (string?)x!["appDomainId"] == first), "Variables include frame domain context.");
                string reference = (string)variables.Single(x => (string?)x!["name"] == "value")!["referenceId"]!;
                await Call("variables", new() { ["frameId"] = frame, ["appDomainId"] = second }, "invalid_request");
                await Call("continue", new() { ["waitForStop"] = false });
                await Ready("c");
                status = await Call("status");
                string replacement = Domain(status, "c");
                ObservationSuite.Require(replacement != first && replacement != second && status["appDomains"]!.AsArray().All(x => (string?)x!["appDomainId"] != first), "Unload removes old identity; recreated domain gets new ID.");
                ObservationSuite.Require((string?)status["breakpoints"]!.AsArray().Single(x => (string?)x!["breakpointId"] == breakpointId)!["state"] == "pending", "Old scoped breakpoint does not bind replacement.");
                await Call("status", new() { ["appDomainId"] = first }, "invalid_request");
                await Call("set_breakpoint", new() { ["file"] = source, ["line"] = line, ["appDomainId"] = replacement });
                File.WriteAllText(Path.Combine(directory, "go-c"), "go");
                status = await Stopped();
                ObservationSuite.Require((string?)status["stop"]!["appDomainId"] == replacement, "Replacement scoped breakpoint hits.");
                int nextThread = (int)status["stop"]!["threadId"]!;
                string nextFrame = (string)(await Call("stack", new() { ["threadId"] = nextThread }))[0]!["frameId"]!;
                await Call("variables", new() { ["frameId"] = nextFrame, ["referenceId"] = reference }, "value_unavailable");
                await Call("detach");
                await target.WaitForExitAsync(timeout.Token);
                ObservationSuite.Require(target.ExitCode == 0 && File.Exists(Path.Combine(directory, "completed")), "Domain fixture safely detached and completed.");
                Console.WriteLine($"AppDomains {architecture} {configuration}: same names, scoped hits, cross-domain frames, variables, unload/recreation passed.");
            }
            finally
            {
                foreach (string label in new[] { "a", "b", "c" }) File.WriteAllText(Path.Combine(directory, "go-" + label), "cleanup");
                await connection.DisposeAsync();
                if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(); }
            }
        }
    }
}
