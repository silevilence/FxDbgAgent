using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

try
{
string bundle = Path.GetFullPath(args[0]);
if (args.Length > 1 && args[1] == "exception-filters")
{
    await ExceptionFilterSuite.Run(bundle,Path.GetFullPath(args[2]),args[3]); return 0;
}
if (args.Length > 1 && args[1] == "handshake")
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    int failures = 0;
    await Task.WhenAll(Enumerable.Range(0, 4).Select(async worker =>
    {
        for (int iteration = 0; iteration < 50; iteration++)
        {
            try { await using var connection = await McpTestConnection.Create(bundle, deadline.Token); }
            catch (McpProtocolException error) { Interlocked.Increment(ref failures); Console.WriteLine($"Handshake {worker}/{iteration}: {error.Message}"); }
        }
    }));
    Console.WriteLine($"Handshake stress: {failures} failures / 200 connections.");
    if (failures != 0) throw new InvalidOperationException("MCP initialized notification must precede immediate tools/list.");
    return 0;
}
if (args.Length > 1 && args[1] == "evaluation")
{
    await EvaluationSuite.Run(bundle,Path.GetFullPath(args[2]),args[3]); return 0;
}
if (args.Length > 1 && args[1] == "iis-lifecycle")
{
    await IisLifecycleSuite.Run(bundle, Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[4]));
    return 0;
}
if (args.Length > 1 && args[1] == "iis")
{
    await IisSuite.Run(bundle, Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[4]));
    return 0;
}
if (args.Length > 1 && args[1] == "services")
{
    await ServiceSuite.Run(bundle, Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[4]));
    return 0;
}
if (args.Length > 1 && (args[1] == "permissions" || args[1] == "permissions-restricted"))
{
    await ServiceIisPermissionsSuite.Run(bundle, Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[4]), args[1] == "permissions-restricted");
    return 0;
}
if (args.Length > 1 && args[1] == "appdomains")
{
    await AppDomainSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "source-mappings")
{
    await SourceMappingSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "paging")
{
    await PagingSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "mvp")
{
    await MvpSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "agent-bridge")
{
    await AgentBridge.Run(bundle, Path.GetFullPath(args[2]));
    return 0;
}
if (args.Length > 1 && args[1] == "lifecycle")
{
    await LifecycleSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "resources")
{
    await ResourceSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "observations")
{
    await ObservationSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
if (args.Length > 1 && args[1] == "execution")
{
    await ExecutionSuite.Run(bundle, Path.GetFullPath(args[2]), args[3]);
    return 0;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using var connection = await McpTestConnection.Create(bundle, timeout.Token);
var client = connection.Client;
var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
Require(tools.Count == 15 && tools.Any(x=>x.Name=="debug_evaluate") && tools.Any(x=>x.Name=="debug_configure_exceptions"), "Thirteen existing tools plus evaluation and exception filters are advertised.");
Require(tools.All(x => x.ProtocolTool.InputSchema.GetProperty("type").GetString() == "object"), "Input schemas must be objects.");
Require(tools.All(x => x.ProtocolTool.OutputSchema.HasValue), "Output schemas are present.");
var missing = await client.CallToolAsync("debug_status", new Dictionary<string, object?> { ["sessionId"] = Guid.NewGuid().ToString() }, cancellationToken: timeout.Token);
Require(missing.IsError == true && missing.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "session_not_found", "Missing session returns a structured tool error.");
var invalid = await client.CallToolAsync("debug_status", new Dictionary<string, object?> { ["sessionId"] = "invalid", ["extra"] = true }, cancellationToken: timeout.Token);
Require(invalid.IsError == true && invalid.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "invalid_request", "Unknown arguments rejected.");
try { await client.CallToolAsync("debug_missing", cancellationToken: timeout.Token); throw new InvalidOperationException("Unknown tool was accepted."); }
catch (McpProtocolException error) { Require(error.ErrorCode == McpErrorCode.InvalidParams, "Unknown tool is a protocol error."); }
Console.WriteLine("Official MCP client: handshake, 15 schemas (13 compatible tools plus evaluation and exception filters), business errors, unknown tool passed.");
return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
