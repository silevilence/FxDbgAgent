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
    int maxSessions = 8, maxCalls = 32, maxControls = 8;
    string logLevel = "info";
    string? logFile = null;
    var seenOptions = new HashSet<string>(StringComparer.Ordinal);
    for (int index = 0; index < args.Length; index += 2)
    {
        if (index + 1 == args.Length || !seenOptions.Add(args[index])) throw new ArgumentException("Each startup option requires exactly one value.");
        if (args[index] == "--engine-dir") { engineDirectory = Path.GetFullPath(args[index + 1]); continue; }
        if (args[index] == "--log-level") { logLevel = args[index + 1]; continue; }
        if (args[index] == "--log-file") { logFile = args[index + 1]; continue; }
        if (args[index] == "--value-logs")
        {
            if (args[index + 1] != "off") throw new ArgumentException("Value logging is disabled. --value-logs only accepts off.");
            continue;
        }
        if (!int.TryParse(args[index + 1], out int limit)) throw new ArgumentException("Startup limits must be integers.");
        switch (args[index])
        {
            case "--max-sessions": maxSessions = limit; break;
            case "--max-calls": maxCalls = limit; break;
            case "--max-control-calls": maxControls = limit; break;
            default: throw new ArgumentException("Usage: fxdbg-mcp [--engine-dir DIR] [--max-sessions 1..8] [--max-calls 1..32] [--max-control-calls 1..8]");
        }
    }
    var limits = new SessionServiceLimits(maxSessions, maxCalls, maxControls);
    using var audit = new AuditLog(logLevel, logFile);
    foreach (string file in new[] { "FxDbg.Engine.x86.exe", "FxDbg.Engine.x64.exe", "FxDbg.Interop.dll", "ClrDebug.dll", "FxDbg.Engine.Protocol.dll" })
        if (!File.Exists(Path.Combine(engineDirectory, file))) throw new ArgumentException("Engine bundle is incomplete. Use --engine-dir with a published engines directory.");
    string manifestPath = Path.Combine(engineDirectory, "engine-manifest.json");
    if (!File.Exists(manifestPath)) throw new ArgumentException("Engine manifest is missing. Run eng/publish-mcp.ps1 to create a complete bundle.");
    var manifest = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? throw new ArgumentException("Invalid engine manifest.");
    if (manifest.Length == 0 || manifest.Any(file => string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || !File.Exists(Path.Combine(engineDirectory, file))))
        throw new ArgumentException("Engine dependencies are missing; republish the complete bundle.");
    using var host = new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
        new EngineProcessPaths(Path.Combine(engineDirectory, "FxDbg.Engine.x86.exe"), Path.Combine(engineDirectory, "FxDbg.Engine.x64.exe")));
    await using var sessions = new DebugSessionService(host, limits);
    using var shutdown = new CancellationTokenSource();
    int initialized = 0;
    void RequireInitialized()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new McpProtocolException("Complete initialize and notifications/initialized first.", McpErrorCode.InvalidRequest);
    }
    Console.CancelKeyPress += (_, input) => { input.Cancel = true; shutdown.Cancel(); };
    await using var transport = new BoundedStdioTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());
    using var disconnected = transport.Disconnected.Register(shutdown.Cancel);
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
                    if (error is FxDbgException { Code: FxDbgErrorCode.RateLimited }) result["error"]!["retryAfterMs"] = 1000;
                }
                CallToolResult Wrap(JsonObject value) => new() { IsError = !value["ok"]!.GetValue<bool>(), StructuredContent = JsonSerializer.SerializeToElement(value),
                    Content = [new TextContentBlock { Text = value.ToJsonString() }] };
                CallToolResult wrapped = Wrap(result);
                if (JsonSerializer.SerializeToUtf8Bytes(wrapped, McpJsonUtilities.DefaultOptions).Length > BoundedStdioTransport.MaximumBytes - 16384)
                    wrapped = Wrap(new JsonObject { ["ok"] = false, ["sessionId"] = sessionId,
                        ["error"] = new JsonObject { ["code"] = "invalid_request", ["message"] = "MCP result exceeds 4 MiB. Reduce page count, depth or string length and retry." } });
                audit.ToolCompleted(tool.Name, (JsonObject)JsonNode.Parse(wrapped.StructuredContent!.Value.GetRawText())!);
                return wrapped;
            }
        }
    };
    options.Filters.Message.IncomingFilters.Add(next => async (context, token) =>
    {
        try
        {
            if (context.JsonRpcMessage is JsonRpcNotification { Method: "notifications/initialized" } && context.Server.ClientInfo is not null)
                Volatile.Write(ref initialized, 1);
            await next(context, token);
        }
        finally { transport.MessageHandled(); }
    });
    await using var server = McpServer.Create(transport, options);
    async Task CheckClientAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(3000, shutdown.Token);
                if (Volatile.Read(ref initialized) == 0) continue;
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                await server.SendRequestAsync(new JsonRpcRequest { Id = new RequestId("fxdbg-health-" + Guid.NewGuid().ToString("N")), Method = "ping" }, probe.Token);
            }
        }
        catch (Exception) { shutdown.Cancel(); }
    }
    Task health = CheckClientAsync();
    try { await server.RunAsync(shutdown.Token); }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    finally { shutdown.Cancel(); await health; }
    if (transport.Failed) Console.Error.WriteLine("MCP transport failed; check message size, JSON framing or disconnected output.");
    return transport.Failed ? 1 : 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error is ArgumentException ? error.Message : "MCP Host failed; check the engine bundle and protocol input.");
    return 1;
}
