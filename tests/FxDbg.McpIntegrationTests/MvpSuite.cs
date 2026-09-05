using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Acceptance through the published stdio entry point, including WinForms and rejection paths.</summary>
internal static class MvpSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string evidence = Path.Combine(root, "artifacts/stage2-validation", "mvp-" + configuration);
        Directory.CreateDirectory(evidence);
        var outcomes = new List<object>();
        bool passed = false;
        try
        {
            foreach (string architecture in new[] { "x86", "x64" })
            foreach (string kind in new[] { "Console", "WinForms" })
            foreach (string scenario in new[] { "observe", "late", "exception" })
            {
                await Scenario(bundle, root, configuration, architecture, kind, scenario, evidence, deadline.Token);
                outcomes.Add(new { architecture, kind, scenario, passed = true });
                Console.WriteLine($"MCP MVP: {configuration} {kind} {architecture} {scenario} passed.");
            }
            await Rejections(bundle, root, configuration, evidence, deadline.Token);
            outcomes.Add(new { scenario = "13 tool negative schemas, CoreCLR, cross architecture, mismatched Windows PDB", passed = true });
            passed = true;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { passed, configuration, outcomes, atUtc = DateTimeOffset.UtcNow }));
        }
    }

    private static async Task Scenario(string bundle, string root, string configuration, string architecture, string kind, string scenario, string evidence, CancellationToken token)
    {
        await using var connection = await McpTestConnection.Create(bundle, token);
        string directory = Path.Combine(evidence, kind + "-" + architecture + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(root, "tests/Debuggees/Shared/EndToEndScenarios.cs");
        var created = await ObservationSuite.Call(connection.Client, "launch", new() { ["exe"] = Executable(root, configuration, kind, architecture),
            ["args"] = new[] { "--scenario", scenario, directory }, ["stopAtEntry"] = true }, token);
        string session = created["sessionId"]!.GetValue<string>();
        ObservationSuite.Require((string?)created["result"]?["architecture"] == architecture, "MCP must auto-route the real architecture.");
        using Process target = Process.GetProcessById(created["result"]!["processId"]!.GetValue<int>());
        _ = target.Handle;
        async Task<JsonNode> Call(string method, Dictionary<string, object?>? args = null, string? error = null)
        {
            args ??= new(); args["sessionId"] = session;
            var response = await ObservationSuite.Call(connection.Client, method, args, token, error);
            await File.AppendAllTextAsync(Path.Combine(directory, "calls.jsonl"), JsonSerializer.Serialize(new { method, args, response }) + "\n", token);
            return response["result"] ?? response;
        }
        try
        {
            JsonNode? breakpoint = null;
            if (scenario == "observe") breakpoint = await Call("set_breakpoint", new() { ["file"] = source, ["line"] = Line(source, "E2E_BREAKPOINT") });
            if (scenario == "late")
            {
                string library = Path.Combine(root, $"tests/Debuggees/Fx40.LateModule/bin/{configuration}/net40/Fx40.LateModule.dll");
                File.Copy(library, Path.Combine(directory, "Fx40.LateModule.dll"));
                File.Copy(Path.ChangeExtension(library, ".pdb"), Path.Combine(directory, "Fx40.LateModule.pdb"));
                string lateSource = Path.Combine(root, "tests/Debuggees/Fx40.LateModule/LateCode.cs");
                breakpoint = await Call("set_breakpoint", new() { ["file"] = lateSource, ["line"] = Line(lateSource, "LATE_BREAKPOINT") });
                ObservationSuite.Require((string?)breakpoint["state"] == "pending", "Late module starts pending.");
            }
            JsonNode stop = (await Call("continue"))["stop"]!;
            int thread = stop["threadId"]!.GetValue<int>();
            if (scenario == "exception")
            {
                JsonNode error = stop["exception"]!;
                ObservationSuite.Require((string?)stop["reason"] == "exception" && error["isUnhandled"]!.GetValue<bool>(), "Default stop must be unhandled.");
                ObservationSuite.Require(error["typeName"]!.GetValue<string>().EndsWith("ScenarioException") && (string?)error["message"] == "e2e-unhandled-message", "Exception uses safe stored type/message.");
                ObservationSuite.Require((int?)error["throwLocation"]?["line"] == Line(source, "E2E_EXCEPTION") && error["stack"]!.AsArray().Count > 0, "Exception has source and stack.");
                await Call("terminate");
            }
            else
            {
                ObservationSuite.Require((string?)stop["reason"] == "breakpoint" && (string?)stop["breakpointId"] == (string?)breakpoint!["breakpointId"], "Stop identity matches breakpoint.");
                JsonNode status = await Call("status");
                string moduleName = scenario == "late" ? "Fx40.LateModule.dll" : Path.GetFileName(Executable(root, configuration, kind, architecture));
                ObservationSuite.Require(status["modules"]!.AsArray().Any(x => (string?)x!["name"] == moduleName && (string?)x["symbolStatus"] == "loaded"), "Application Windows PDB is loaded.");
                if (scenario == "observe")
                {
                    ObservationSuite.Require((int?)stop["location"]?["line"] == Line(source, "E2E_BREAKPOINT"), "Source location is exact.");
                    var stack = (await Call("stack", new() { ["threadId"] = thread, ["count"] = 32 })).AsArray();
                    ObservationSuite.Require(stack.Count >= 14, "MCP exposes at least fourteen recursive managed frames.");
                    var values = (await Call("variables", new() { ["frameId"] = (string?)stack[0]!["frameId"], ["maxDepth"] = 1 })).AsArray();
                    JsonNode Value(string name) => values.Single(x => (string?)x!["name"] == name)!;
                    ObservationSuite.Require((string?)Value("number")["displayValue"] == "42" && (string?)Value("localNumber")["displayValue"] == "84" &&
                        (string?)Value("message")["displayValue"] == "hello-framework" && (string?)Value("localText")["displayValue"] == "local-value", "Arguments, locals and strings are observed.");
                    ObservationSuite.Require(Value("node")["children"]!.AsArray().Any(x => (string?)x!["name"] == "Label" && (string?)x["displayValue"] == "node-label") &&
                        (string?)Value("userCodeCalls")["displayValue"] == "0", "One field level without user evaluation.");
                    foreach (string step in new[] { "over", "into", "out" })
                    {
                        JsonNode stepped = (await Call("step", new() { ["threadId"] = thread, ["kind"] = step }))["stop"]!;
                        ObservationSuite.Require((string?)stepped["reason"] == "step", "Source step completed.");
                        if (step == "into") ObservationSuite.Require(stepped["methodName"]!.GetValue<string>().EndsWith("AddOne"), "Into entered AddOne.");
                        if (step == "out") ObservationSuite.Require(stepped["methodName"]!.GetValue<string>().EndsWith("Observe"), "Out returned to Observe.");
                        thread = stepped["threadId"]!.GetValue<int>();
                    }
                }
                await Call("remove_breakpoint", new() { ["breakpointId"] = (string?)breakpoint!["breakpointId"] });
                ObservationSuite.Require((string?)(await Call("continue"))["stop"]?["reason"] == "processExit", "Continue observes real natural exit.");
                ObservationSuite.Require(File.ReadAllText(Path.Combine(directory, "completed")) == "ok", "Target completed with no evaluated user code.");
            }
            await target.WaitForExitAsync(token);
            ObservationSuite.Require(target.ExitCode == 0, "Owned fixture exits cleanly.");
            await Call("set_breakpoint", new() { ["file"] = source, ["line"] = 1 }, "invalid_session_state");
            await Call("detach", error: "invalid_session_state");
            await connection.DisposeAsync();
        }
        finally { await CleanupTarget(target); }
    }

    private static async Task Rejections(string bundle, string root, string configuration, string evidence, CancellationToken token)
    {
        await using var connection = await McpTestConnection.Create(bundle, token);
        foreach (var tool in await connection.Client.ListToolsAsync(cancellationToken: token))
            await ObservationSuite.Call(connection.Client, tool.Name.Substring("debug_".Length), new() { ["unexpected"] = true }, token, "invalid_request");
        // The runner itself is a live x64 CoreCLR process owned by this test invocation.
        await ObservationSuite.Call(connection.Client, "attach", new() { ["pid"] = Environment.ProcessId }, token, "core_clr_not_supported");
        foreach (string architecture in new[] { "x86", "x64" })
        {
            string executable = Executable(root, configuration, "Console", architecture);
            await ObservationSuite.Call(connection.Client, "launch", new() { ["exe"] = executable, ["arch"] = architecture == "x86" ? "x64" : "x86" }, token, "architecture_mismatch");
            string directory = Path.Combine(evidence, "pdb-mismatch-" + architecture + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string copy = Path.Combine(directory, Path.GetFileName(executable));
            File.Copy(executable, copy);
            File.Copy(executable + ".config", copy + ".config");
            File.Copy(Path.ChangeExtension(Executable(root, configuration, "Console", architecture == "x86" ? "x64" : "x86"), ".pdb"), Path.ChangeExtension(copy, ".pdb"));
            var created = await ObservationSuite.Call(connection.Client, "launch", new() { ["exe"] = copy, ["args"] = new[] { "--scenario", "gated", directory } }, token);
            string session = created["sessionId"]!.GetValue<string>();
            using Process target = Process.GetProcessById(created["result"]!["processId"]!.GetValue<int>());
            try
            {
                JsonNode status;
                do
                {
                    status = await ObservationSuite.Call(connection.Client, "status", new() { ["sessionId"] = session }, token);
                    if (status["result"]!["modules"]!.AsArray().Any(x => (string?)x!["name"] == Path.GetFileName(copy))) break;
                    await Task.Delay(20, token);
                } while (true);
                JsonNode module = status["result"]!["modules"]!.AsArray().Single(x => (string?)x!["name"] == Path.GetFileName(copy))!;
                ObservationSuite.Require((string?)module["symbolStatus"] == "mismatch" && !string.IsNullOrWhiteSpace((string?)module["diagnostic"]), "Mismatched PDB has explicit status and actionable diagnostic: " + module);
                await File.WriteAllTextAsync(Path.Combine(directory, "status.json"), status.ToJsonString(), token);
                await ObservationSuite.Call(connection.Client, "attach", new() { ["pid"] = target.Id, ["arch"] = architecture == "x86" ? "x64" : "x86" }, token, "architecture_mismatch");
                await ObservationSuite.Call(connection.Client, "detach", new() { ["sessionId"] = session }, token);
                await File.WriteAllTextAsync(Path.Combine(directory, "go"), "go", token);
                await target.WaitForExitAsync(token);
                ObservationSuite.Require(File.ReadAllText(Path.Combine(directory, "completed")) == "ok", "Mismatched symbols never stop target from running after detach.");
            }
            finally { await CleanupTarget(target); }
        }
        Console.WriteLine("MCP MVP: all thirteen negative schemas, CoreCLR, launch/attach cross architecture and PDB mismatch passed.");
    }

    private static string Executable(string root, string configuration, string kind, string architecture) =>
        Path.Combine(root, $"tests/Debuggees/Fx40.{kind}.{architecture}/bin/{configuration}/net40/Fx40.{kind}.{architecture}.exe");
    private static int Line(string source, string marker) => File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains("// " + marker)).index + 1;
    private static async Task CleanupTarget(Process target)
    {
        if (target.HasExited) return;
        target.Kill(); // Only this test's fixture, after all lifecycle assertions; never a process tree.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await target.WaitForExitAsync(timeout.Token);
    }
}
