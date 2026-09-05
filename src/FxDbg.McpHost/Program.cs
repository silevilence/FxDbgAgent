using System.Text.Json;
using System.Text.Json.Nodes;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;
using FxDbg.Host.Sessions;
using FxDbg.McpHost;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

try
{
    string engineDirectory = Path.Combine(AppContext.BaseDirectory, "engines");
    if (args.Length != 0)
    {
        if (args.Length != 2 || args[0] != "--engine-dir") throw new ArgumentException("Usage: fxdbg-mcp [--engine-dir DIR]");
        engineDirectory = Path.GetFullPath(args[1]);
    }
    foreach (string file in new[] { "FxDbg.Engine.x86.exe", "FxDbg.Engine.x64.exe", "FxDbg.Interop.dll", "ClrDebug.dll", "FxDbg.Engine.Protocol.dll" })
        if (!File.Exists(Path.Combine(engineDirectory, file))) throw new ArgumentException("Engine bundle is incomplete. Use --engine-dir with a published engines directory.");
    string manifestPath = Path.Combine(engineDirectory, "engine-manifest.json");
    if (!File.Exists(manifestPath)) throw new ArgumentException("Engine manifest is missing. Run eng/publish-mcp.ps1 to create a complete bundle.");
    var manifest = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? throw new ArgumentException("Invalid engine manifest.");
    if (manifest.Length == 0 || manifest.Any(file => string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || !File.Exists(Path.Combine(engineDirectory, file))))
        throw new ArgumentException("Engine dependencies are missing; republish the complete bundle.");
    using var host = new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
        new EngineProcessPaths(Path.Combine(engineDirectory, "FxDbg.Engine.x86.exe"), Path.Combine(engineDirectory, "FxDbg.Engine.x64.exe")));
    await using var sessions = new DebugSessionService(host);
    using var shutdown = new CancellationTokenSource();
    int initialized = 0;
    void RequireInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new McpProtocolException("Complete initialize and notifications/initialized first.", McpErrorCode.InvalidRequest);
    }
    Console.CancelKeyPress += (_, input) => { input.Cancel = true; shutdown.Cancel(); };
    var options = new McpServerOptions
    {
        ServerInfo = new Implementation { Name = "FxDbg Agent", Version = "0.2.0" }, ProtocolVersion = "2025-11-25",
        InitializationTimeout = TimeSpan.FromSeconds(10),
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (request, _) =>
            {
                RequireInitialized();
                if (request.Params?.Cursor is not null) throw new McpProtocolException("This tool list has no continuation cursor.", McpErrorCode.InvalidParams);
                return ValueTask.FromResult(new ListToolsResult { Tools = ToolCatalog.Tools.ToList() });
            },
            CallToolHandler = async (request, token) =>
            {
                RequireInitialized();
                var call = request.Params ?? throw new ArgumentException("Missing call parameters.");
                Tool tool = ToolCatalog.Find(call.Name);
                JsonElement arguments = JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, JsonElement>());
                string? sessionId = arguments.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                JsonObject result;
                try
                {
                    ToolCatalog.Validate(tool, arguments);
                    var response = await sessions.InvokeAsync(call.Name[6..], Newtonsoft.Json.Linq.JObject.Parse(arguments.GetRawText()), token);
                    result = (JsonObject)JsonNode.Parse(response.ToString())!;
                }
                catch (Exception error) when (error is FxDbgException or ArgumentException or FormatException or OperationCanceledException)
                {
                    string code = error is FxDbgException known ? FxDbgErrorCodeWireName.Format(known.Code)
                        : error is OperationCanceledException ? "operation_cancelled" : "invalid_request";
                    result = new JsonObject { ["ok"] = false, ["sessionId"] = sessionId,
                        ["error"] = new JsonObject { ["code"] = code, ["message"] = error is FxDbgException ? error.Message : "Invalid or cancelled request; check the tool schema." } };
                }
                return new CallToolResult { IsError = !result["ok"]!.GetValue<bool>(), StructuredContent = JsonSerializer.SerializeToElement(result),
                    Content = [new TextContentBlock { Text = result.ToJsonString() }] };
            }
        }
    };
    options.Filters.Message.IncomingFilters.Add(next => async (context, token) =>
    {
        if (context.JsonRpcMessage is JsonRpcNotification { Method: "notifications/initialized" } && context.Server.ClientInfo is not null)
            Volatile.Write(ref initialized, 1);
        await next(context, token);
    });
    await using var transport = new StdioServerTransport(options);
    await using var server = McpServer.Create(transport, options);
    await server.RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error is ArgumentException ? error.Message : "MCP Host failed; check the engine bundle and protocol input.");
    return 1;
}
