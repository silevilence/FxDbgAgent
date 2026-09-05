using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class ObservationSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        string source = Path.Combine(root, "tests/Debuggees/Shared/EndToEndScenarios.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains("// E2E_BREAKPOINT")).index + 1;
        foreach (string architecture in new[] { "x86", "x64" })
        foreach (bool attach in new[] { false, true })
        {
            string gate = Path.GetFullPath(Path.Combine(root, "artifacts/mcp-observation", Guid.NewGuid().ToString("N"), "gate with spaces"));
            Directory.CreateDirectory(gate);
            string original = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            string executable = Path.Combine(gate, Path.GetFileName(original));
            string secret = "MCP-SECRET-" + Guid.NewGuid().ToString("N");
            string auditPath = Path.Combine(gate, "host-audit.jsonl");
            foreach (string extension in new[] { "", ".config" }) if (File.Exists(original + extension)) File.Copy(original + extension, executable + extension);
            File.Copy(Path.ChangeExtension(original, ".pdb"), Path.ChangeExtension(executable, ".pdb"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Process? target = null;
            McpTestConnection? connection = null;
            McpClient? client = null;
            try
            {
                connection = await McpTestConnection.Create(bundle, timeout.Token, gate, ["--log-level", "debug", "--log-file", auditPath, "--value-logs", "off"]);
                client = connection.Client;
                string session;
                if (attach)
                {
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = gate,
                        RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("--scenario"); start.ArgumentList.Add("gated"); start.ArgumentList.Add(gate);
                    start.Environment["FXDBG_MCP_TEST"] = secret;
                    target = Process.Start(start)!;
                    await Until(() => File.Exists(Path.Combine(gate, "ready")), timeout.Token);
                    var created = await Call(client, "attach", new() { ["pid"] = target.Id }, timeout.Token);
                    session = created["sessionId"]!.GetValue<string>();
                    Require(created["result"]!["architecture"]!.GetValue<string>() == architecture, "Attach auto architecture.");
                }
                else
                {
                    var created = await Call(client, "launch", new()
                    {
                        ["exe"] = executable, ["args"] = new[] { "--scenario", "gated", gate }, ["cwd"] = gate,
                        ["env"] = new Dictionary<string, string> { ["FXDBG_MCP_TEST"] = secret }
                    }, timeout.Token);
                    session = created["sessionId"]!.GetValue<string>();
                    target = Process.GetProcessById(created["result"]!["processId"]!.GetValue<int>());
                    Require(created["result"]!["architecture"]!.GetValue<string>() == architecture, "Launch auto architecture.");
                    Require(!string.IsNullOrEmpty(created["result"]!["runtimeFileVersion"]!.GetValue<string>()), "Actual CLR file version.");
                    await Until(() => File.Exists(Path.Combine(gate, "ready")), timeout.Token);
                }
                await Until(() => File.ReadAllText(Path.Combine(gate, "ready")) == secret + "|" + gate, timeout.Token);
                await Call(client, "attach", new() { ["pid"] = target.Id }, timeout.Token, "already_debugged");
                await Call(client, "launch", new() { ["exe"] = executable, ["arch"] = architecture == "x86" ? "x64" : "x86" }, timeout.Token, "architecture_mismatch");
                async Task<JsonNode> Invoke(string tool, Dictionary<string, object?>? arguments = null, string? error = null)
                {
                    arguments ??= new(); arguments["sessionId"] = session;
                    return await Call(client, tool, arguments, timeout.Token, error);
                }
                await Invoke("threads", error: "invalid_session_state");
                var bp = (await Invoke("set_breakpoint", new() { ["file"] = source, ["line"] = line, ["enabled"] = false }))["result"]!;
                string breakpointId = bp["breakpointId"]!.GetValue<string>();
                Require(bp["enabled"]!.GetValue<bool>() == false, "Disabled creation.");
                Require(bp["state"]!.GetValue<string>() is "pending" or "verified" or "moved", "Initial Windows PDB binding state.");
                while (true)
                {
                    var bindingStatus = (await Invoke("status"))["result"]!;
                    var rebound = bindingStatus["breakpoints"]!.AsArray().Single(x => (string?)x!["breakpointId"] == breakpointId)!;
                    if ((string?)rebound["state"] is "verified" or "moved") break;
                    Require((string?)rebound["state"] == "pending", "Pending binding must become verified: " + rebound);
                    await Task.Delay(20, timeout.Token);
                }
                await Invoke("set_breakpoint", new() { ["breakpointId"] = breakpointId, ["enabled"] = true });
                var pending = (await Invoke("set_breakpoint", new() { ["file"] = Path.Combine(gate, "not-loaded.cs"), ["line"] = 1 }))["result"]!;
                Require(pending["state"]!.GetValue<string>() == "pending", "Not-loaded source stays pending.");
                await Invoke("remove_breakpoint", new() { ["breakpointId"] = pending["breakpointId"]!.GetValue<string>() });
                File.WriteAllText(Path.Combine(gate, "go"), "go");
                JsonNode? status = null;
                while (true)
                {
                    status = (await Invoke("status"))["result"]!;
                    if (status["target"]!["sessionState"]!.GetValue<string>() == "stopped") break;
                    await Task.Delay(20, timeout.Token);
                }
                Require(status["stop"]!["reason"]!.GetValue<string>() == "breakpoint", "Stopped reason is explicit.");
                Require(status["modules"]!.AsArray().Any(x => x!["symbolStatus"]!.GetValue<string>() == "loaded"), "Windows symbols visible.");
                Require(status["breakpoints"]!.AsArray().Count == 1, "Deleted breakpoint absent from snapshot.");
                Require(status["eventSequence"]!.GetValue<long>() > 0, "Snapshot has engine sequence.");
                int thread = status["stop"]!["threadId"]!.GetValue<int>();
                var threads = (await Invoke("threads"))["result"]!.AsArray();
                Require(threads.Any(x => x!["threadId"]!.GetValue<int>() == thread), "Stop thread exists.");
                var stack = (await Invoke("stack", new() { ["threadId"] = thread }))["result"]!.AsArray();
                Require(stack.Count >= 10, "At least ten managed frames.");
                string frame = stack[0]!["frameId"]!.GetValue<string>();
                var variables = (await Invoke("variables", new() { ["frameId"] = frame }))["result"]!.AsArray();
                Require(variables.Any(x => (string?)x!["name"] == "number" && (string?)x["displayValue"] == "42"), "Parameter value.");
                Require(variables.Any(x => (string?)x!["name"] == "localNumber" && (string?)x["displayValue"] == "84"), "Local value.");
                Require(variables.Any(x => (string?)x!["name"] == "message" && x!["displayValue"]!.GetValue<string>().Contains("hello-framework")), "String value.");
                var node = variables.Single(x => (string?)x!["name"] == "node")!;
                var members = (await Invoke("variables", new() { ["frameId"] = frame, ["referenceId"] = node["referenceId"]!.GetValue<string>(), ["maxDepth"] = 0 }))["result"]!.AsArray();
                Require(members.Any(x => (string?)x!["name"] == "Nothing" && (string?)x["status"] == "null"), "Null stays distinct from unavailable.");
                var self = members.Single(x => (string?)x!["name"] == "Self")!;
                Require((string?)self["referenceId"] == (string?)node["referenceId"] && self["children"]!.AsArray().Count == 0, "Circular identity is preserved without recursive expansion.");
                await Invoke("variables", new() { ["frameId"] = frame, ["referenceId"] = "unknown-reference" }, "value_unavailable");
                var texts = members.Single(x => (string?)x!["name"] == "LargeTexts")!;
                var largeArgs = new Dictionary<string, object?> { ["frameId"] = frame, ["referenceId"] = texts["referenceId"]!.GetValue<string>(), ["count"] = 129, ["maxStringLength"] = 32768 };
                await Invoke("variables", largeArgs, "invalid_request");
                largeArgs["count"] = 64; // Fits the Engine frame, exceeds the duplicated MCP structured/text envelope.
                await Invoke("variables", largeArgs, "invalid_request");
                largeArgs["count"] = 1;
                Require((await Invoke("variables", largeArgs))["result"]!.AsArray().Count == 1, "Oversized page can be reduced without losing the session.");
                await Invoke("variables", new() { ["frameId"] = "not-a-frame" }, "frame_not_found");
                await Invoke("remove_breakpoint", new() { ["breakpointId"] = breakpointId });
                await Invoke("remove_breakpoint", new() { ["breakpointId"] = breakpointId }, "breakpoint_not_found");
                await connection.DisposeAsync(); client = null;
                await target.WaitForExitAsync(timeout.Token);
                Require(File.Exists(Path.Combine(gate, "completed")), "EOF detaches the stopped target so it finishes.");
                string diagnostics = connection.StandardError + File.ReadAllText(auditPath);
                Require(!diagnostics.Contains(secret) && !diagnostics.Contains("TARGET_STD") && !diagnostics.Contains("hello-framework") && !diagnostics.Contains("local-value"), "No target output, env or variable values in debug diagnostics.");
                foreach (string record in File.ReadAllLines(auditPath)) Require(JsonNode.Parse(record)!["command"] is not null, "File audit contains only metadata JSON.");
                Console.WriteLine($"MCP observation: {architecture} {(attach ? "attach" : "launch")} passed.");
            }
            finally
            {
                if (connection is not null) await connection.DisposeAsync();
                if (target is not null) { if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(); } target.Dispose(); }
            }
        }
    }

    internal static async Task<JsonNode> Call(McpClient client, string method, Dictionary<string, object?> arguments, CancellationToken token, string? expectedError = null)
    {
        CallToolResult response = await client.CallToolAsync("debug_" + method, arguments, cancellationToken: token);
        JsonNode envelope = JsonNode.Parse(response.StructuredContent!.Value.GetRawText())!;
        Require(JsonNode.DeepEquals(envelope, JsonNode.Parse(((TextContentBlock)response.Content[0]).Text)), "Structured and text JSON must agree.");
        if (expectedError is not null) Require(response.IsError == true && (string?)envelope["error"]?["code"] == expectedError, $"{method} expected {expectedError}: {envelope}");
        else Require(response.IsError != true && envelope["ok"]!.GetValue<bool>(), $"{method} failed: {envelope}");
        return envelope;
    }
    private static async Task Until(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(20, token);
    }
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
