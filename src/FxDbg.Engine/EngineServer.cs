using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Events;
using FxDbg.Core.Evaluation;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using FxDbg.Engine.Scheduling;
using FxDbg.Interop;
using Newtonsoft.Json.Linq;

namespace FxDbg.Engine;

internal sealed class EngineServer
{
    private readonly string sessionId;
    private readonly BlockingCollection<JObject> events = new(1024);
    private FrameworkDebugSession? session;
    private SingleThreadCommandScheduler scheduler = null!;
    private RpcPeer peer = null!;
    private long lastDomainSequence;
    private long eventSequence;
    private bool started;

    private EngineServer(string sessionId) => this.sessionId = sessionId;

    internal static int Run(string[] args)
    {
        if (args.Length != 3 || !Guid.TryParse(args[2], out Guid id) || id == Guid.Empty)
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Serve requires a local pipe name and session ID.");
        var server = new EngineServer(args[2]);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(10000);
        using (server.scheduler = new SingleThreadCommandScheduler("FxDbg.Engine.CommandScheduler", server.Pump))
        using (server.peer = new RpcPeer(pipe, args[2], server.Handle))
        {
            Task sender = Task.Run(server.SendEvents);
            try { server.peer.Completion.GetAwaiter().GetResult(); }
            finally
            {
                // The callback owner performs Detach even if the Host disappeared mid-command.
                try
                {
                    Task<bool> cleanup = server.scheduler.EnqueueAsync(_ => { Program.DisposeSession(server.session); server.session = null; return true; }, TimeSpan.FromSeconds(5));
                    if (!cleanup.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Engine cleanup exceeded its deadline.");
                    cleanup.GetAwaiter().GetResult();
                }
                finally { server.events.CompleteAdding(); sender.Wait(TimeSpan.FromSeconds(4)); }
            }
        }
        server.events.Dispose();
        return 0;
    }

    private async Task<JToken> Handle(string method, JObject args, CancellationToken cancellationToken)
    {
        int milliseconds = Integer(args, "commandTimeoutMs", Integer(args, "timeoutMs", 10000));
        if (milliseconds <= 0 || milliseconds > 300000) throw Invalid("Invalid command timeout.");
        using EvaluationBudget? evaluationBudget = method == "evaluate" ? new EvaluationBudget(Integer(args, "evaluationTimeoutMs", 250), cancellationToken, milliseconds) : null;
        int schedulingMilliseconds = evaluationBudget is null ? milliseconds : Math.Min(milliseconds, Integer(args, "evaluationTimeoutMs", 250));
        return await scheduler.EnqueueAsync(token =>
        {
            try { return Dispatch(method, args, token, TimeSpan.FromMilliseconds(milliseconds), evaluationBudget); }
            catch (FxDbgException error) when (error.Code == FxDbgErrorCode.OperationCancelled && !cancellationToken.IsCancellationRequested)
            { throw new FxDbgException(FxDbgErrorCode.OperationTimedOut, error.Message, error); }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException || error is Newtonsoft.Json.JsonException)
            { throw Invalid("Command parameters have an invalid type or value."); }
            finally { PublishEvents(); }
        }, TimeSpan.FromMilliseconds(schedulingMilliseconds), cancellationToken).ConfigureAwait(false);
    }

    private JToken Dispatch(string method, JObject args, CancellationToken token, TimeSpan timeout, EvaluationBudget? evaluationBudget = null)
    {
        token.ThrowIfCancellationRequested();
        if (method == "start")
        {
            if (started) throw Invalid("This Engine already started a session.");
            if (Integer(args, "protocolVersion", 0) != WireJson.ProtocolVersion) throw Invalid("Unsupported Engine protocol version.");
            string[] options = args["arguments"]?.ToObject<string[]>() ?? throw Invalid("Start arguments are required.");
            if (options.Length > 512) throw Invalid("Too many start arguments.");
            EngineOptions parsed = EngineOptions.Parse(options);
            if (parsed.SessionId.ToString() != sessionId) throw new FxDbgException(FxDbgErrorCode.SessionNotFound, "Start session ID mismatch.");
            EngineTargetValidator.RequireCurrentArchitecture(parsed.Architecture);
            var sourceMapper = new SourcePathMapper(args["sourceMappings"]?.ToObject<SourcePathMapping[]>());
            session = parsed.Mode == EngineMode.Launch ? Program.Launch(parsed, token) : Program.Attach(parsed, token);
            session.ConfigureSourceMappings(sourceMapper);
            started = true;
            session.BreakpointChanged += change => QueueEvent("breakpointChanged", WireJson.Value(change));
            return new JObject { ["protocolVersion"] = WireJson.ProtocolVersion, ["target"] = WireJson.Value(session.Target) };
        }
        FrameworkDebugSession current = session ?? throw new FxDbgException(FxDbgErrorCode.InvalidSessionState, "Engine session is not active.");
        object? result;
        switch (method)
        {
            case "state":
                PublishEvents();
                result = new JObject { ["target"] = WireJson.Value(current.Target), ["stop"] = WireJson.Value(current.CurrentStop), ["eventSequence"] = eventSequence,
                    ["appDomains"] = WireJson.Value(current.GetAppDomains()) };
                if (Boolean(args, "includeDetails", false))
                {
                    ((JObject)result)["breakpoints"] = WireJson.Value(current.GetBreakpoints());
                    ((JObject)result)["modules"] = WireJson.Value(current.GetModules());
                    if (args["appDomainId"] is not null)
                    {
                        string selected = Text(args, "appDomainId");
                        if (!current.GetAppDomains().Any(item => item.AppDomainId == selected)) throw Invalid("AppDomain is unknown or unloaded.");
                        ((JObject)result)["appDomains"] = WireJson.Value(current.GetAppDomains().Where(item => item.AppDomainId == selected).ToArray());
                        ((JObject)result)["modules"] = WireJson.Value(current.GetModules().Where(item => item.AppDomainId == selected).ToArray());
                        ((JObject)result)["breakpoints"] = WireJson.Value(current.GetBreakpoints().Where(item => item.AppDomainId == selected || item.AppDomainId is null).ToArray());
                    }
                }
                break;
            case "break.set": result = current.SetBreakpoint(new SourceLocation(Text(args, "file"), Integer(args, "line", 0)), Boolean(args, "enabled", true), OptionalText(args, "appDomainId")); break;
            case "break.list": result = current.GetBreakpoints(); break;
            case "break.remove": current.RemoveBreakpoint(new BreakpointId(Text(args, "breakpointId"))); result = new { removed = true }; break;
            case "break.enable": result = current.SetBreakpointEnabled(new BreakpointId(Text(args, "breakpointId")), Boolean(args, "enabled", true)); break;
            case "continue": current.Continue(); result = current.Target; break;
            case "pause": current.Pause(token); result = current.CurrentStop; break;
            case "step":
                if (!Enum.TryParse(Text(args, "kind"), true, out StepKind kind) || !Enum.IsDefined(typeof(StepKind), kind)) throw Invalid("Step kind must be into, over or out.");
                current.Step(Integer(args, "threadId", 0), kind, token); result = current.Target; break;
            case "wait": result = current.WaitForStop(timeout, token); break;
            case "threads": result = current.GetThreads(token, OptionalText(args, "appDomainId")); break;
            case "stack": result = current.GetStack(Integer(args, "threadId", 0), Integer(args, "start", 0), Integer(args, "count", 32), token, OptionalText(args, "appDomainId")); break;
            case "variables":
                result = current.GetVariables(new FrameId(Text(args, "frameId")), args["referenceId"]?.Type == JTokenType.String ? new VariableReferenceId(Text(args, "referenceId")) : null,
                    Integer(args, "start", 0), Integer(args, "count", 100), Integer(args, "maxDepth", 1), Integer(args, "maxStringLength", 256), token, OptionalText(args, "appDomainId")); break;
            case "evaluate":
                result = current.Evaluate(new FrameId(Text(args, "frameId")), Text(args, "expression"), Integer(args, "evaluationTimeoutMs", 250),
                    Integer(args, "maxDepth", 1), Integer(args, "count", 100), Integer(args, "maxStringLength", 256), token, OptionalText(args, "appDomainId"), evaluationBudget); break;
            case "exceptions.configure":
                if (args["rules"] is not null && args["rules"] is not JArray) throw Invalid("Exception rules must be an array.");
                if (args["rules"] is JArray ruleArray && ruleArray.Count > 64) throw Invalid("At most 64 exception rules are allowed.");
                ExceptionStopConfiguration configured = current.ConfigureExceptionStops(Boolean(args, "firstChance", false),
                    args["rules"]?.ToObject<ExceptionTypeRule[]>(WireJson.CreateSerializer()));
                result = new { configured = true, configured.FirstChance, configured.Rules }; break;
            case "modules": result = current.GetModules(); break;
            case "symbols.refresh": current.RefreshSymbols(); result = current.GetModules(); break;
            case "detach":
                Program.DisposeSession(current);
                result = current.Target; break;
            case "terminate": current.Terminate(timeout, token); result = current.Target; break;
            default: throw Invalid("Unknown Engine command.");
        }
        return WireJson.Value(result);
    }

    private void Pump(CancellationToken token)
    {
        try
        {
            if (session is not null && session.State != DebugSessionState.Terminated && session.State != DebugSessionState.Failed)
                session.PumpNextCallback(TimeSpan.FromMilliseconds(10), token);
            PublishEvents();
        }
        catch (Exception)
        {
            try { Program.DisposeSession(session); } finally { session = null; peer?.Dispose(); }
        }
    }

    private void PublishEvents()
    {
        if (session is null) return;
        foreach (EngineEvent item in session.Events.Where(item => item.Sequence > lastDomainSequence))
        {
            if (item.Sequence != lastDomainSequence + 1) { peer.Dispose(); return; }
            lastDomainSequence = item.Sequence;
            string kind = item is StoppedEvent ? "stopped" : item is ModuleChangedEvent ? "moduleChanged" : item is AppDomainChangedEvent ? "appDomainChanged" : item is ThreadChangedEvent ? "threadChanged" : "stateChanged";
            JToken data = WireJson.Value(item);
            if (item is SessionStateChangedEvent changed && changed.CurrentState == DebugSessionState.Terminated
                && session.CurrentStop?.Reason == StopReason.ProcessExit)
                data["stop"] = WireJson.Value(session.CurrentStop);
            QueueEvent(kind, data);
        }
    }

    private void QueueEvent(string kind, JToken data)
    {
        if (!events.TryAdd(new JObject { ["sequence"] = ++eventSequence, ["kind"] = kind, ["data"] = data })) peer.Dispose();
    }

    private async Task SendEvents()
    {
        try
        {
            foreach (JObject item in events.GetConsumingEnumerable())
                await peer.NotifyAsync("event", item).ConfigureAwait(false);
        }
        catch (Exception) { peer.Dispose(); }
    }

    private static string Text(JObject args, string name) => args[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)args[name])
        ? (string)args[name]! : throw Invalid("Required string parameter: " + name);
    private static string? OptionalText(JObject args, string name) => args[name] is null ? null : Text(args, name);
    private static int Integer(JObject args, string name, int fallback) => args[name] is null ? fallback : args[name]!.Type == JTokenType.Integer
        ? (int)args[name]! : throw Invalid("Integer parameter required: " + name);
    private static bool Boolean(JObject args, string name, bool fallback) => args[name] is null ? fallback : args[name]!.Type == JTokenType.Boolean
        ? (bool)args[name]! : throw Invalid("Boolean parameter required: " + name);
    private static FxDbgException Invalid(string message) => new(FxDbgErrorCode.InvalidRequest, message);
}
