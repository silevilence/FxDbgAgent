using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Transparent local file-to-official-MCP-client bridge. No debugging workflow or backend lives here.</summary>
internal static class AgentBridge
{
    internal static async Task Run(string bundle, string directory)
    {
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        await using var connection = await McpTestConnection.Create(bundle, deadline.Token);
        var done = new HashSet<string>(StringComparer.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { bridgePid = Environment.ProcessId, mcpPid = connection.ProcessId,
            protocol = "2025-11-25", sdk = "2.2.0", startedAtUtc = DateTimeOffset.UtcNow }), deadline.Token);
        while (true)
        {
            foreach (string path in Directory.GetFiles(directory, "*.request.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!done.Add(path)) continue;
                JsonNode request = JsonNode.Parse(await File.ReadAllTextAsync(path, deadline.Token))!;
                await File.AppendAllTextAsync(Path.Combine(directory, "calls.jsonl"), new JsonObject { ["atUtc"] = DateTimeOffset.UtcNow, ["request"] = request.DeepClone() }.ToJsonString() + "\n", deadline.Token);
                JsonNode result;
                string kind = request["kind"]!.GetValue<string>();
                if (kind == "close")
                {
                    await connection.DisposeAsync();
                    await File.WriteAllTextAsync(path.Replace(".request.json", ".response.json"), "{\"closed\":true}", deadline.Token);
                    return;
                }
                try
                {
                    if (kind == "tools/list")
                        result = JsonSerializer.SerializeToNode((await connection.Client.ListToolsAsync(cancellationToken: deadline.Token)).Select(x => x.ProtocolTool), ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
                    else if (kind == "tools/call")
                    {
                        var arguments = request["arguments"]!.AsObject().ToDictionary(x => x.Key, x => (object?)JsonSerializer.SerializeToElement(x.Value));
                        var response = await connection.Client.CallToolAsync(request["name"]!.GetValue<string>(), arguments, cancellationToken: deadline.Token);
                        result = JsonSerializer.SerializeToNode(response, ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
                    }
                    else throw new ArgumentException("Bridge kind must be tools/list, tools/call or close.");
                }
                catch (Exception error) { result = new JsonObject { ["bridgeError"] = error.GetType().Name, ["message"] = error.Message }; }
                await File.AppendAllTextAsync(Path.Combine(directory, "calls.jsonl"), new JsonObject { ["atUtc"] = DateTimeOffset.UtcNow, ["responseTo"] = Path.GetFileName(path), ["response"] = result.DeepClone() }.ToJsonString() + "\n", deadline.Token);
                string responsePath = path.Replace(".request.json", ".response.json");
                await File.WriteAllTextAsync(responsePath + ".tmp", result.ToJsonString(), deadline.Token);
                File.Move(responsePath + ".tmp", responsePath, overwrite: true);
            }
            await Task.Delay(25, deadline.Token);
        }
    }
}
