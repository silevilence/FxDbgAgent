using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FxDbg.Host.Sessions;

/// <summary>Mutated only while holding its owning observation's gate.</summary>
internal sealed class ExecutionOperation
{
    private readonly Func<DateTimeOffset> utcNow;
    internal ExecutionOperation(Func<DateTimeOffset>? utcNow = null)
    {
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        CreatedAtUtc = this.utcNow();
    }
    internal string Id { get; } = Guid.NewGuid().ToString("D");
    internal DateTimeOffset CreatedAtUtc { get; }
    internal DateTimeOffset? FinishedAtUtc { get; private set; }
    internal long AfterSequence { get; set; } = long.MaxValue;
    internal bool Finishing { get; set; }
    internal Task? Watchdog { get; set; }
    internal bool IsCompleted => FinishedAtUtc.HasValue;
    internal TaskCompletionSource<JObject> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JObject result = new() { ["state"] = "running" };

    internal JObject Snapshot()
    {
        var snapshot = (JObject)result.DeepClone();
        snapshot["operationId"] = Id;
        snapshot["createdAtUtc"] = CreatedAtUtc;
        if (FinishedAtUtc.HasValue) snapshot["finishedAtUtc"] = FinishedAtUtc.Value;
        return snapshot;
    }

    internal void Finish(string state, JToken? stop = null, string? code = null, string? message = null)
    {
        if (IsCompleted) return;
        FinishedAtUtc = utcNow();
        result = new JObject { ["state"] = state };
        if (stop is not null) result["stop"] = stop.DeepClone();
        if (code is not null) result["error"] = new JObject { ["code"] = code, ["message"] = message };
        Completion.TrySetResult(Snapshot());
    }
}
