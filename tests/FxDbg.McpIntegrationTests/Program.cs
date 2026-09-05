using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

string bundle = Path.GetFullPath(args[0]);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet", Arguments = [Path.Combine(bundle, "fxdbg-mcp.dll")], Name = "FxDbg integration"
});
await using var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: timeout.Token);
var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
Require(tools.Count == 13, "Exactly thirteen tools are advertised.");
Require(tools.All(x => x.ProtocolTool.InputSchema.GetProperty("type").GetString() == "object"), "Input schemas must be objects.");
Require(tools.All(x => x.ProtocolTool.OutputSchema.HasValue), "Output schemas are present.");
var missing = await client.CallToolAsync("debug_status", new Dictionary<string, object?> { ["sessionId"] = Guid.NewGuid().ToString() }, cancellationToken: timeout.Token);
Require(missing.IsError == true && missing.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "session_not_found", "Missing session returns a structured tool error.");
var invalid = await client.CallToolAsync("debug_status", new Dictionary<string, object?> { ["sessionId"] = "invalid", ["extra"] = true }, cancellationToken: timeout.Token);
Require(invalid.IsError == true && invalid.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "invalid_request", "Unknown arguments rejected.");
try { await client.CallToolAsync("debug_missing", cancellationToken: timeout.Token); throw new InvalidOperationException("Unknown tool was accepted."); }
catch (McpProtocolException error) { Require(error.ErrorCode == McpErrorCode.InvalidParams, "Unknown tool is a protocol error."); }
Console.WriteLine("Official MCP client: handshake, 13 schemas, business errors, unknown tool passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
