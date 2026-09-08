using System.Collections.Concurrent;
using FxDbg.Core.Errors;
using FxDbg.Host.Sessions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FxDbg.DapHost;

internal sealed partial class DapServer(DebugSessionService sessions, Stream input, Stream output)
{
    private readonly DapTransport transport = new(input, output);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> requests = new();
    private readonly List<Task> tasks = [];
    private string? sessionId;
    private JObject? pendingStart;
    private bool initialized, configured, ended, stopAtEntry;
    private bool targetStopped;
    private bool linesStartAt1 = true, columnsStartAt1 = true;
    private string? lastStop;
    private readonly Dictionary<string, string> breakpointStates = new(StringComparer.Ordinal);

    internal async Task RunAsync()
    {
        using var disconnected = transport.Disconnected.Register(lifetime.Cancel);
        Task poll = PollAsync();
        try
        {
            while (await transport.ReadAsync(lifetime.Token) is { } packet)
            {
                if ((string?)packet["type"] != "request" || packet["seq"]?.Type != JTokenType.Integer || (int)packet["seq"]! <= 0 || packet["command"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Expected a DAP request with a positive seq.");
                int id = (int)packet["seq"]!;
                if ((string?)packet["command"] == "cancel")
                {
                    if ((int?)packet["arguments"]?["requestId"] is { } cancelled && requests.TryGetValue(cancelled, out var request))
                        try { request.Cancel(); } catch (ObjectDisposedException) { }
                    await Respond(packet, new JObject());
                    continue;
                }
                tasks.RemoveAll(task => task.IsCompleted);
                bool control = (string?)packet["command"] is "disconnect" or "pause" or "terminate";
                if (requests.Count >= (control ? 36 : 32)) { await Fail(packet, "rate_limited", "Too many pending DAP requests."); continue; }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (!requests.TryAdd(id, cancellation)) { cancellation.Dispose(); await Fail(packet, "invalid_request", "Duplicate in-flight request seq."); continue; }
                tasks.Add(ProcessAsync(packet, cancellation));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(tasks);
            await poll;
            foreach (var cancellation in requests.Values) cancellation.Dispose();
            requests.Clear();
            await sessions.DisposeAsync();
            lifetime.Dispose();
            commands.Dispose();
        }
        if (transport.Failed) throw new IOException("DAP output disconnected or stalled.");
    }

    private async Task ProcessAsync(JObject packet, CancellationTokenSource cancellation)
    {
        bool entered = false;
        try
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            await commands.WaitAsync(cancellation.Token); entered = true;
            await Dispatch(packet, cancellation.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                string code = error is FxDbgException domain ? FxDbgErrorCodeWireName.Format(domain.Code) : error is OperationCanceledException ? "operation_cancelled" : "invalid_request";
                string message = error is FxDbgException or ArgumentException ? error.Message : "Request failed; inspect session state before retrying.";
                try { await Fail(packet, code, message); } catch { lifetime.Cancel(); }
            }
        }
        finally
        {
            if (entered) commands.Release();
            if (pendingStart != packet)
            {
                requests.TryRemove((int)packet["seq"]!, out _);
                cancellation.Dispose();
            }
        }
    }

    private async Task Dispatch(JObject packet, CancellationToken token)
    {
        string command = (string)packet["command"]!;
        JObject args = packet["arguments"] is null ? new() : packet["arguments"] as JObject ?? throw Invalid("arguments must be an object.");
        if (command == "initialize")
        {
            if (initialized) throw Invalid("Already initialized.");
            if ((string?)args["pathFormat"] is { } format && format != "path") throw Invalid("Only native file paths are supported.");
            linesStartAt1 = (bool?)args["linesStartAt1"] ?? true;
            columnsStartAt1 = (bool?)args["columnsStartAt1"] ?? true;
            initialized = true;
            await Respond(packet, new JObject { ["supportsConfigurationDoneRequest"] = true, ["supportsTerminateRequest"] = true,
                ["supportsCancelRequest"] = true, ["supportsDelayedStackTraceLoading"] = true, ["supportsEvaluateForHovers"] = true,
                ["supportsExceptionFilterOptions"] = true,
                ["supportsConditionalBreakpoints"] = true, ["supportsHitConditionalBreakpoints"] = true,
                ["exceptionBreakpointFilters"] = new JArray(new JObject { ["filter"] = "firstChance", ["label"] = "First-chance managed exceptions",
                    ["default"] = false, ["supportsCondition"] = true,
                    ["conditionDescription"] = "Type rules separated by semicolons: exact:Full.Type;namespace:Prefix;derived:Base.Type" }) });
            return;
        }
        if (!initialized) throw Invalid("initialize must complete first.");
        if (command is "launch" or "attach")
        {
            if (sessionId is not null || pendingStart is not null || ended) throw Invalid("One target per DAP connection.");
            if ((bool?)args["noDebug"] == true) throw Invalid("noDebug is not supported.");
            JObject launch = command == "launch" ? new() { ["exe"] = Text(args, "program"), ["stopAtEntry"] = true } : new() { ["pid"] = Integer(args, "processId") };
            foreach (string field in new[] { "args", "cwd", "env", "arch", "sourceMappings" }) if (args[field] is not null) launch[field] = args[field]!.DeepClone();
            var created = await sessions.InvokeAsync(command, launch, token);
            sessionId = (string)created["sessionId"]!;
            stopAtEntry = (bool?)args["stopAtEntry"] ?? false;
            if (command == "attach") await Invoke("pause", new(), token);
            pendingStart = packet;
            await Event("process", new JObject { ["name"] = command == "launch" ? Path.GetFileName((string)launch["exe"]!) : "Attached .NET Framework process",
                ["systemProcessId"] = created["result"]!["processId"]!.DeepClone(), ["isLocalProcess"] = true, ["startMethod"] = command });
            await Event("initialized", new JObject());
            return;
        }
        if (command == "disconnect")
        {
            bool alreadyEnded = ended;
            if (sessionId is not null && !ended)
                await Invoke((bool?)args["terminateDebuggee"] == true ? "terminate" : "detach", new(), token);
            ended = true; ClearHandles();
            await Respond(packet, new JObject());
            if (pendingStart is not null) await FinishStart("Disconnected during configuration.");
            if (!alreadyEnded) await Event("terminated", new JObject());
            return;
        }
        if (command == "terminate" && ended) { await Respond(packet, new()); return; }
        if (sessionId is null || ended) throw Invalid("No active DAP target.");
        if (command == "configurationDone")
        {
            if (configured || pendingStart is null) throw Invalid("No pending configuration.");
            if (requests.TryGetValue((int)pendingStart["seq"]!, out var startCancellation)) startCancellation.Token.ThrowIfCancellationRequested();
            if (!stopAtEntry) await Resume("continue", new(), token);
            configured = true;
            await Respond(packet, new JObject());
            await FinishStart();
            return;
        }
        if (command == "setBreakpoints") { await Respond(packet, await SetBreakpoints(args, token)); return; }
        if (command == "setExceptionBreakpoints")
        {
            await Respond(packet, await SetExceptionBreakpoints(args, token)); return;
        }
        if (!configured) throw Invalid("configurationDone must complete first.");
        if ((bool?)args["singleThread"] == true) throw Invalid("Only all-thread execution control is supported.");
        if ((string?)args["granularity"] == "instruction") throw Invalid("Only source-level stepping is supported.");
        if (command is "threads" or "stackTrace" or "scopes" or "variables" or "evaluate") await RefreshStatus(token);
        JObject result;
        switch (command)
        {
            case "continue": await Resume("continue", new(), token); result = new() { ["allThreadsContinued"] = true }; break;
            case "next": case "stepIn": case "stepOut":
                await Resume("step", new() { ["threadId"] = Integer(args, "threadId"), ["kind"] = command == "next" ? "over" : command == "stepIn" ? "into" : "out" }, token);
                result = new(); break;
            case "pause": await Invoke("pause", new(), token); result = new(); break;
            case "terminate": await Invoke("terminate", new(), token); result = new(); break;
            case "threads": result = await Threads(token); break;
            case "stackTrace": result = await Stack(args, token); break;
            case "scopes": result = Scopes(args); break;
            case "variables": result = await Variables(args, token); break;
            case "evaluate": result = await Evaluate(args, token); break;
            default: throw Invalid("Unsupported DAP request: " + command);
        }
        await Respond(packet, result);
    }

    private async Task Resume(string method, JObject args, CancellationToken token)
    {
        args["waitForStop"] = false; args["timeoutMs"] = 240000;
        await Invoke(method, args, token);
        ClearHandles(); lastStop = null; targetStopped = false;
        await Event("continued", new JObject { ["allThreadsContinued"] = true, ["threadId"] = (int?)args["threadId"] ?? 0 });
    }

    private async Task<JToken> Invoke(string method, JObject args, CancellationToken token)
    {
        args["sessionId"] = sessionId;
        return (await sessions.InvokeAsync(method, args, token))["result"]!;
    }

    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(50, lifetime.Token);
                await commands.WaitAsync(lifetime.Token);
                try
                {
                    if (pendingStart is not null && requests.TryGetValue((int)pendingStart["seq"]!, out var start) && start.IsCancellationRequested)
                    {
                        try { await Invoke("detach", new(), lifetime.Token); }
                        finally { ended = true; await FinishStart("Launch/attach configuration was cancelled or timed out."); await Event("terminated", new()); }
                    }
                    if (sessionId is null || !configured || ended) continue;
                    await RefreshStatus(lifetime.Token);
                }
                finally { commands.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { lifetime.Cancel(); }
    }

    private async Task FinishStart(string? error = null)
    {
        var pending = pendingStart!;
        pendingStart = null;
        if (requests.TryRemove((int)pending["seq"]!, out var cancellation)) cancellation.Dispose();
        if (error is null) await Respond(pending, new()); else await Fail(pending, "operation_cancelled", error);
    }

    private async Task RefreshStatus(CancellationToken token)
    {
        var status = await Invoke("status", new(), token);
        targetStopped = (string?)status["target"]?["sessionState"] == "stopped";
        foreach (var breakpoint in status["breakpoints"] ?? new JArray())
        {
            string id = (string)breakpoint["breakpointId"]!;
            if (!breakpointNumbers.TryGetValue(id, out int number)) continue;
            string state = ToBreakpoint(breakpoint, number).ToString(Formatting.None);
            if (breakpointStates.TryGetValue(id, out string? previous) && state != previous)
                await Event("breakpoint", new JObject { ["reason"] = "changed", ["breakpoint"] = ToBreakpoint(breakpoint, number) });
            breakpointStates[id] = state;
        }
        if ((string?)status["target"]?["sessionState"] is "terminated" or "failed")
        {
            ended = true; ClearHandles();
            if ((string?)status["target"]?["sessionState"] == "failed") await Event("output", new JObject { ["category"] = "stderr", ["output"] = "Debugger session failed; inspect the target before reconnecting.\n" });
            await Event("terminated", new JObject());
        }
        else if (status["stop"] is JObject stop)
        {
            string signature = stop.ToString(Formatting.None);
            if (signature == lastStop) return;
            ClearHandles(); lastStop = signature;
            string reason = (string?)stop["reason"] switch { "breakpoint" => "breakpoint", "step" => "step", "exception" => "exception", "entry" => "entry", _ => "pause" };
            var body = new JObject { ["reason"] = reason, ["allThreadsStopped"] = true };
            if ((int?)stop["threadId"] is > 0) body["threadId"] = stop["threadId"]!.DeepClone();
            await Event("stopped", body);
        }
    }

    private Task Respond(JObject request, JObject body) => transport.SendAsync(new JObject { ["type"] = "response", ["request_seq"] = request["seq"]!.DeepClone(),
        ["command"] = request["command"]!.DeepClone(), ["success"] = true, ["body"] = body }, lifetime.Token);
    private Task Fail(JObject request, string code, string message) => transport.SendAsync(new JObject { ["type"] = "response", ["request_seq"] = request["seq"]!.DeepClone(),
        ["command"] = request["command"]!.DeepClone(), ["success"] = false, ["message"] = code,
        ["body"] = new JObject { ["error"] = new JObject { ["id"] = 1, ["format"] = message, ["showUser"] = true } } }, lifetime.Token);
    private Task Event(string name, JObject body) => transport.SendAsync(new JObject { ["type"] = "event", ["event"] = name, ["body"] = body }, lifetime.Token);
    private static string Text(JObject args, string name) => args[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)args[name]) ? (string)args[name]! : throw Invalid("Required string: " + name);
    private static int Integer(JObject args, string name, int? fallback = null) => args[name] is null && fallback.HasValue ? fallback.Value : args[name]?.Type == JTokenType.Integer ? (int)args[name]! : throw Invalid("Required integer: " + name);
    private static ArgumentException Invalid(string message) => new(message);
}
