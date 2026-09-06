using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using FxDbg.Host.Engine;
using Newtonsoft.Json.Linq;

namespace FxDbg.Host.Sessions;

/// <summary>Protocol-independent session ownership and observation over the single Engine backend.</summary>
public sealed partial class DebugSessionService : IAsyncDisposable
{
    private readonly IEngineSessionHost engine;
    private readonly ConcurrentDictionary<SessionId, Observation> sessions = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task eventPump;
    private readonly SessionServiceLimits limits;
    private readonly CallAdmission admission;
    private readonly object creationGate = new();
    private Task? disposal;

    public DebugSessionService(EngineProcessHost engine, SessionServiceLimits? limits = null)
        : this((IEngineSessionHost)engine, limits) { }

    internal DebugSessionService(IEngineSessionHost engine, SessionServiceLimits? limits = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.limits = limits ?? new SessionServiceLimits();
        admission = new CallAdmission(this.limits);
        eventPump = PumpEventsAsync();
    }

    public async Task<JToken> InvokeAsync(string method, JObject arguments, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var lease = admission.Enter(method, linked.Token);
        TimeSpan timeout = TimeSpan.FromMilliseconds((int?)arguments["timeoutMs"] ?? 10000);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(4)) throw Invalid("Timeout must be 1-240000 milliseconds.");
        if (method is "launch" or "attach") return await CreateAsync(method, arguments, timeout, linked.Token).ConfigureAwait(false);
        var id = new SessionId(Guid.Parse((string?)arguments["sessionId"] ?? throw Invalid("sessionId is required.")));
        Observation observation = Find(id);
        if (method == "status")
        {
            string? operationId = (string?)arguments["operationId"];
            ExecutionOperation? requestedOperation = null;
            JObject? snapshot = null;
            lock (observation.Gate)
            {
                PruneOperations(observation, DateTimeOffset.UtcNow);
                if (operationId is not null && !observation.Operations.TryGetValue(operationId, out requestedOperation)) throw new FxDbgException(FxDbgErrorCode.OperationNotFound, "Operation is unknown, expired, or belongs to another session.");
                if (observation.ClosedAtUtc.HasValue || observation.Closing) snapshot = TerminalSnapshot(observation);
            }
            if (snapshot is null)
            {
                try
                {
                    // Cancelling a read-only status request must not close a running operation's pipe.
                    Task<JToken> query = engine.InvokeAsync(id, "state", new JObject { ["includeDetails"] = true }, timeout, lifetime.Token);
                    lease.HoldUntil(query);
                    _ = query.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    snapshot = (JObject)await query.WaitAsync(timeout, linked.Token).ConfigureAwait(false);
                }
                catch (TimeoutException) { throw new FxDbgException(FxDbgErrorCode.OperationTimedOut, "Status query timed out; the running operation keeps its original deadline."); }
                catch (FxDbgException error) when (error.Code is FxDbgErrorCode.EngineExited or FxDbgErrorCode.TransportDisconnected or FxDbgErrorCode.SessionNotFound)
                {
                    lock (observation.Gate)
                    {
                        FailObservation(observation, error.Code);
                        snapshot = TerminalSnapshot(observation);
                    }
                }
            }
            snapshot["snapshotAtUtc"] = DateTimeOffset.UtcNow;
            snapshot["lastStop"] = snapshot["stop"]?.DeepClone();
            if (snapshot["lastStop"]?.Type is null or JTokenType.Null)
                lock (observation.Gate) snapshot["lastStop"] = observation.LastStop?.DeepClone();
            if ((string?)snapshot["target"]?["sessionState"] != "stopped") snapshot["stop"] = null;
            lock (observation.Gate)
            {
                snapshot.Remove("operation");
                if (requestedOperation is not null) snapshot["operation"] = requestedOperation.Snapshot();
                snapshot["activeOperationId"] = observation.ActiveOperation is { IsCompleted: false } active ? active.Id : null;
                observation.LastSnapshot = (JObject)snapshot.DeepClone();
            }
            return Envelope(id, snapshot);
        }
        lock (observation.Gate) RequireActive(observation);
        if (method is "continue" or "step") return await RunAsync(observation, method, arguments, timeout, linked.Token).ConfigureAwait(false);
        if (method is "pause" or "detach" or "terminate") return await ControlAsync(observation, method, arguments, timeout, linked.Token).ConfigureAwait(false);
        string command = method switch
        {
            "set_breakpoint" => arguments["breakpointId"] is null ? "break.set" : "break.enable",
            "remove_breakpoint" => "break.remove",
            "threads" => "threads", "stack" => "stack", "variables" => "variables",
            _ => throw Invalid("This command is not implemented in the current stage.")
        };
        JToken result = await engine.InvokeAsync(id, command, arguments, timeout, linked.Token).ConfigureAwait(false);
        return Envelope(id, result);
    }

    private async Task<JToken> CreateAsync(string method, JObject args, TimeSpan timeout, CancellationToken token)
    {
        SessionId id = SessionId.New();
        var observation = new Observation(id);
        lock (creationGate)
        {
            token.ThrowIfCancellationRequested();
            PruneSessions(extraRecords: 1);
            if (sessions.Values.Count(x => { lock (x.Gate) return !x.ClosedAtUtc.HasValue; }) >= limits.ActiveSessions)
                throw CallAdmission.RateLimited();
            if (!sessions.TryAdd(id, observation)) throw Invalid("Could not allocate session identity.");
        }
        try
        {
            TargetArchitecture architecture = Enum.TryParse((string?)args["arch"] ?? "auto", true, out TargetArchitecture parsed) && Enum.IsDefined(parsed)
                ? parsed : throw Invalid("Invalid architecture.");
            var target = method == "launch"
                ? await engine.LaunchAsync(new LaunchRequest(id, (string?)args["exe"] ?? throw Invalid("exe is required."), args["args"]?.ToObject<string[]>(),
                    (string?)args["cwd"], args["env"]?.ToObject<Dictionary<string, string>>(), architecture, (bool?)args["stopAtEntry"] ?? false, timeout, args["sourceMappings"]?.ToObject<SourcePathMapping[]>()), token).ConfigureAwait(false)
                : await engine.AttachAsync(new AttachRequest(id, (int?)args["pid"] ?? 0, architecture, timeout, args["sourceMappings"]?.ToObject<SourcePathMapping[]>()), token).ConfigureAwait(false);
            lock (observation.Gate) { observation.Target = (JObject)WireJson.Value(target); observation.Started = true; }
            return Envelope(id, WireJson.Value(target));
        }
        catch { sessions.TryRemove(id, out _); throw; }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                foreach (Observation observation in sessions.Values)
                {
                    lock (observation.Gate) { if (!observation.Started || observation.ClosedAtUtc.HasValue || observation.Closing) continue; }
                    try
                    {
                        foreach (JObject message in engine.DrainEvents(observation.Id))
                        {
                            lock (observation.Gate)
                            {
                                long sequence = (long)message["sequence"]!;
                                if (sequence != observation.Sequence + 1) throw new FxDbgException(FxDbgErrorCode.TransportDisconnected, "Engine event sequence has a gap.");
                                observation.Sequence = sequence;
                                observation.ObservedAtUtc = DateTimeOffset.UtcNow;
                                if ((string?)message["kind"] == "stopped" || (string?)message["data"]?["stop"]?["reason"] == "processExit")
                                {
                                    observation.LastStop = message["data"]?["stop"]?.DeepClone();
                                    if (observation.ActiveOperation is { IsCompleted: false, Finishing: false } operation && sequence > operation.AfterSequence)
                                    {
                                        operation.Finish("completed", observation.LastStop);
                                        PruneOperations(observation, DateTimeOffset.UtcNow);
                                    }
                                    if ((string?)observation.LastStop?["reason"] == "processExit") MarkClosed(observation, "terminated");
                                }
                                if ((string?)message["kind"] == "stateChanged" && message["data"]?["currentState"] is JToken state)
                                    observation.Target!["sessionState"] = state.DeepClone();
                            }
                        }
                    }
                    catch (FxDbgException error) { lock (observation.Gate) if (!observation.Closing) FailObservation(observation, error.Code); }
                    bool ended;
                    lock (observation.Gate) ended = observation.ClosedAtUtc.HasValue;
                    if (ended) await Task.Run(() => engine.CloseSession(observation.Id)).ConfigureAwait(false);
                }
                PruneSessions();
                await Task.Delay(10, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private Observation Find(SessionId id)
    {
        PruneSessions();
        if (!sessions.TryGetValue(id, out Observation? observation)) throw new FxDbgException(FxDbgErrorCode.SessionNotFound, "Session was not found in this Host.");
        return observation;
    }

    private void PruneSessions(int extraRecords = 0)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ended = new List<(SessionId Id, DateTimeOffset Ended)>();
        foreach (Observation observation in sessions.Values)
        {
            lock (observation.Gate) if (observation.ClosedAtUtc.HasValue) ended.Add((observation.Id, observation.ClosedAtUtc.Value));
        }
        foreach (SessionId id in SessionRetention.Evictions(ended, sessions.Count, now, extraRecords)) sessions.TryRemove(id, out _);
    }

    private static void FailObservation(Observation observation, FxDbgErrorCode code)
    {
        if (observation.ClosedAtUtc.HasValue || observation.Closing) return;
        observation.Failure = code;
        MarkClosed(observation, "failed");
        observation.ActiveOperation?.Finish("failed", code: FxDbgErrorCodeWireName.Format(code), message: "Engine connection failed; inspect the target before reattaching.");
        PruneOperations(observation, DateTimeOffset.UtcNow);
    }

    private static JObject TerminalSnapshot(Observation observation)
    {
        var snapshot = observation.LastSnapshot is null ? new JObject() : (JObject)observation.LastSnapshot.DeepClone();
        snapshot["target"] = observation.Target?.DeepClone();
        snapshot["stop"] = null;
        snapshot["lastStop"] = observation.LastStop?.DeepClone();
        snapshot["eventSequence"] = observation.Sequence;
        snapshot["closing"] = observation.Closing;
        if (observation.Failure is FxDbgErrorCode failure) snapshot["failureCode"] = FxDbgErrorCodeWireName.Format(failure);
        return snapshot;
    }

    private static JObject Envelope(SessionId id, JToken value) => new() { ["ok"] = true, ["sessionId"] = id.ToString(), ["result"] = value };
    private static FxDbgException Invalid(string message) => new(FxDbgErrorCode.InvalidRequest, message);

    public ValueTask DisposeAsync()
    {
        lock (creationGate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        foreach (Observation observation in sessions.Values)
            lock (observation.Gate) observation.ActiveOperation?.Finish("cancelled", code: "operation_cancelled", message: "Host is closing.");
        await eventPump.ConfigureAwait(false);
        await Task.WhenAll(sessions.Values.SelectMany(x => { lock (x.Gate) return x.Operations.Values.Select(o => o.Watchdog).OfType<Task>().ToArray(); })).ConfigureAwait(false);
        engine.Dispose();
        lifetime.Dispose();
    }

    private sealed class Observation
    {
        internal Observation(SessionId id) { Id = id; }
        internal SessionId Id { get; }
        internal object Gate { get; } = new();
        internal bool Started;
        internal JObject? Target;
        internal long Sequence;
        internal DateTimeOffset ObservedAtUtc;
        internal JToken? LastStop;
        internal FxDbgErrorCode? Failure;
        internal bool Closing;
        internal DateTimeOffset? ClosedAtUtc;
        internal JObject? LastSnapshot;
        internal ExecutionOperation? ActiveOperation;
        internal Dictionary<string, ExecutionOperation> Operations { get; } = new(StringComparer.Ordinal);
    }
}
