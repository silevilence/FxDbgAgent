using System.Diagnostics;
using System.Text.Json.Nodes;

internal static class PagingSuite
{
    internal static async Task Run(string bundle, string root, string configuration)
    {
        var measurements = new List<object>();
        string source = Path.Combine(root, "tests/Debuggees/Shared/EndToEndScenarios.cs");
        int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains("// PAGING_BREAKPOINT")).index + 1;
        foreach (string architecture in new[] { "x86", "x64" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string directory = Path.Combine(root, "artifacts/stage3-validation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string exe = Path.Combine(root, $"tests/Debuggees/Fx40.Console.{architecture}/bin/{configuration}/net40/Fx40.Console.{architecture}.exe");
            await using var connection = await McpTestConnection.Create(bundle, timeout.Token);
            var created = await ObservationSuite.Call(connection.Client, "launch", new()
            {
                ["exe"] = exe, ["args"] = new[] { "--scenario", "paging", directory }, ["stopAtEntry"] = true
            }, timeout.Token);
            string session = (string)created["sessionId"]!;
            using var target = Process.GetProcessById((int)created["result"]!["processId"]!);
            _ = target.Handle;
            async Task<JsonNode> Call(string name, Dictionary<string, object?>? args = null, string? error = null)
            {
                args ??= new(); args["sessionId"] = session;
                return (await ObservationSuite.Call(connection.Client, name, args, timeout.Token, error))[error is null ? "result" : "error"]!;
            }
            try
            {
                await Call("set_breakpoint", new() { ["file"] = source, ["line"] = line });
                await Call("continue");
                var status = await Call("status");
                int thread = (int)status["stop"]!["threadId"]!;
                var stack = (await Call("stack", new() { ["threadId"] = thread })).AsArray();
                string frame = (string)stack[0]!["frameId"]!;
                var cold = Stopwatch.StartNew();
                var roots = (await Call("variables", new() { ["frameId"] = frame, ["maxDepth"] = 0 })).AsArray();
                double coldMs = cold.Elapsed.TotalMilliseconds;
                JsonNode Named(JsonArray items, string name) => items.Single(x => (string?)x!["name"] == name)!;
                string values = (string)Named(roots, "values")["referenceId"]!;
                string graph = (string)Named(roots, "graph")["referenceId"]!;
                ObservationSuite.Require((int)Named(roots, "values")["totalMembers"]! == 100001, "Array exceeds 100000 elements.");
                async Task<JsonArray> Page(string reference, int start = 0, int count = 100, int depth = 0, int length = 256)
                    => (await Call("variables", new() { ["frameId"] = frame, ["referenceId"] = reference,
                        ["start"] = start, ["count"] = count, ["maxDepth"] = depth, ["maxStringLength"] = length })).AsArray();
                var times = new List<double>();
                await Page(values); // Warm transport, metadata and JIT before measuring.
                for (int iteration = 0; iteration < 30; iteration++)
                {
                    int start = (iteration % 3) switch { 0 => 0, 1 => 50000, _ => 100000 };
                    var timer = Stopwatch.StartNew();
                    var page = await Page(values, start);
                    times.Add(timer.Elapsed.TotalMilliseconds);
                    ObservationSuite.Require(page.Count == Math.Min(100, 100001 - start), "Page size including tail.");
                    for (int index = 0; index < page.Count; index++)
                        ObservationSuite.Require((string?)page[index]!["name"] == $"[{start + index}]" && (string?)page[index]!["displayValue"] == (start + index).ToString(), "Exact array page content.");
                }
                ObservationSuite.Require((await Page(values, int.MaxValue)).Count == 0, "Past end and overflow-safe page.");
                var fields = await Page(graph, depth: 8, count: 100, length: 8);
                ObservationSuite.Require((string?)Named(fields, "Self")["referenceId"] == graph && Named(fields, "Self")["children"]!.AsArray().Count == 0, "Cycle identity.");
                ObservationSuite.Require((string?)Named(fields, "Text")["displayValue"] == "ppppppp…", "String truncation.");
                string branches = (string)Named(fields, "Branches")["referenceId"]!;
                long members = 0;
                foreach (var branch in await Page(branches, count: 1024))
                {
                    string reference = (string)branch!["referenceId"]!;
                    members += (int)branch["totalMembers"]!;
                    ObservationSuite.Require((await Page(reference, 1000)).Count == 1, "Object graph leaf tail page.");
                }
                ObservationSuite.Require(members > 100000, "Object graph exceeds 100000 members.");
                await Call("variables", new() { ["frameId"] = frame, ["referenceId"] = values, ["count"] = 1025 }, "invalid_request");
                await Call("variables", new() { ["frameId"] = frame, ["start"] = -1 }, "invalid_request");
                await Call("step", new() { ["threadId"] = thread, ["kind"] = "over" });
                await Call("variables", new() { ["frameId"] = frame }, "frame_not_found");
                string nextFrame = (string)(await Call("stack", new() { ["threadId"] = thread }))[0]!["frameId"]!;
                await Call("variables", new() { ["frameId"] = nextFrame, ["referenceId"] = values }, "value_unavailable");
                await Call("detach");
                await target.WaitForExitAsync(timeout.Token);
                ObservationSuite.Require(target.ExitCode == 0 && File.Exists(Path.Combine(directory, "completed")), "No user code executed during inspection; detach resumes target.");
                times.Sort();
                double p95 = times[(int)Math.Ceiling(times.Count * .95) - 1];
                double maximum = times.Max();
                measurements.Add(new { architecture, configuration, coldMs, p95Ms = p95, maxMs = maximum, samples = times, graphMembers = members });
                ObservationSuite.Require(p95 <= 1000 && maximum <= 5000, $"Page latency: P95={p95:F1}ms max={maximum:F1}ms.");
                Console.WriteLine($"Paging {architecture} {configuration}: cold={coldMs:F1}ms P95={p95:F1}ms max={maximum:F1}ms, graph={members}.");
            }
            finally
            {
                await connection.DisposeAsync();
                if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(CancellationToken.None); }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, $"artifacts/stage3-validation/paging-{configuration}.json"),
            System.Text.Json.JsonSerializer.Serialize(measurements, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
