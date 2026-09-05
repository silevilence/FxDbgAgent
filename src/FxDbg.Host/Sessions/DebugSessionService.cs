using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using FxDbg.Host.Engine;
using Newtonsoft.Json.Linq;

namespace FxDbg.Host.Sessions;

/// <summary>Protocol-independent session ownership and observation over the single Engine backend.</summary>
public sealed partial class DebugSessionService : IAsyncDisposable
{
    private readonly EngineProcessHost engine;
    private readonly ConcurrentDictionary<SessionId, Observation> sessions = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task eventPump;

    public DebugSessionService(EngineProcessHost engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        eventPump = PumpEventsAsync();
    }

    public async Task<JToken> InvokeAsync(string method, JObject arguments, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        TimeSpan timeout = TimeSpan.FromMilliseconds((int?)arguments["timeoutMs"] ?? 10000);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(4)) throw Invalid("Timeout must be 1-240000 milliseconds.");
        if (method is "launch" or "attach") return await CreateAsync(method, arguments, timeout, linked.Token).ConfigureAwait(false);
        var id = new SessionId(Guid.Parse((string?)arguments["sessionId"] ?? throw Invalid("sessionId is required.")));
        Observation observation = Find(id);
        if (method == "status")
        {
            if (arguments["operationId"] is not null) throw Invalid("Operation tracking is not available before execution-control implementation.");
            JObject snapshot = (JObject)await engine.InvokeAsync(id, "state", new JObject { ["includeDetails"] = true }, timeout, linked.Token).ConfigureAwait(false);
            snapshot["snapshotAtUtc"] = DateTimeOffset.UtcNow;
            snapshot["lastStop"] = snapshot["stop"]?.DeepClone();
            if (snapshot["lastStop"]?.Type is null or JTokenType.Null)
                lock (observation.Gate) snapshot["lastStop"] = observation.LastStop?.DeepClone();
            if ((string?)snapshot["target"]?["sessionState"] != "stopped") snapshot["stop"] = null;
            return Envelope(id, snapshot);
        }
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
        if (!sessions.TryAdd(id, observation)) throw Invalid("Could not allocate session identity.");
        try
        {
            TargetArchitecture architecture = Enum.TryParse((string?)args["arch"] ?? "auto", true, out TargetArchitecture parsed) && Enum.IsDefined(parsed)
                ? parsed : throw Invalid("Invalid architecture.");
            var target = method == "launch"
                ? await engine.LaunchAsync(new LaunchRequest(id, (string?)args["exe"] ?? throw Invalid("exe is required."), args["args"]?.ToObject<string[]>(),
                    (string?)args["cwd"], args["env"]?.ToObject<Dictionary<string, string>>(), architecture, (bool?)args["stopAtEntry"] ?? false, timeout), token).ConfigureAwait(false)
                : await engine.AttachAsync(new AttachRequest(id, (int?)args["pid"] ?? 0, architecture, timeout), token).ConfigureAwait(false);
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
                    lock (observation.Gate) { if (!observation.Started || observation.Failure is not null) continue; }
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
                                if ((string?)message["kind"] == "stopped") observation.LastStop = message["data"]?["stop"]?.DeepClone();
                                if ((string?)message["kind"] == "stateChanged" && message["data"]?["currentState"] is JToken state)
                                    observation.Target!["sessionState"] = state.DeepClone();
                            }
                        }
                    }
                    catch (FxDbgException error) { lock (observation.Gate) observation.Failure = error.Code; }
                }
                await Task.Delay(10, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private Observation Find(SessionId id)
    {
        if (!sessions.TryGetValue(id, out Observation? observation)) throw new FxDbgException(FxDbgErrorCode.SessionNotFound, "Session was not found in this Host.");
        lock (observation.Gate)
            if (observation.Failure is FxDbgErrorCode failure) throw new FxDbgException(failure, "Engine connection failed; create a new session after checking the target.");
        return observation;
    }

    private static JObject Envelope(SessionId id, JToken value) => new() { ["ok"] = true, ["sessionId"] = id.ToString(), ["result"] = value };
    private static FxDbgException Invalid(string message) => new(FxDbgErrorCode.InvalidRequest, message);

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await eventPump.ConfigureAwait(false);
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
    }
}
