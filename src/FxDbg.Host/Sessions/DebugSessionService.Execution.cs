using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using Newtonsoft.Json.Linq;

namespace FxDbg.Host.Sessions;

public sealed partial class DebugSessionService
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private async Task<JToken> RunAsync(Observation observation, string command, JObject arguments, TimeSpan timeout, CancellationToken token)
    {
        var operation = new ExecutionOperation();
        lock (observation.Gate)
        {
            RequireActive(observation);
            if (observation.ActiveOperation is { IsCompleted: false })
                throw new FxDbgException(FxDbgErrorCode.InvalidSessionState, "A running operation already exists. Query status or pause it first.");
            observation.ActiveOperation = operation;
            observation.Operations.Add(operation.Id, operation);
            PruneOperations(observation, DateTimeOffset.UtcNow);
        }
        using var cancellation = token.Register(() =>
        {
            lock (observation.Gate)
            {
                if (!operation.IsCompleted)
                {
                    operation.Finishing = true;
                }
            }
        });
        try
        {
            // The Engine takes this snapshot on its command thread after publishing all prior stops.
            // Arm before resuming: even a stop delivered before the resume response is then observed once.
            var state = (JObject)await engine.InvokeAsync(observation.Id, "state", timeout: Remaining(operation, timeout), cancellationToken: token).ConfigureAwait(false);
            if ((string?)state["target"]?["sessionState"] != "stopped")
                throw new FxDbgException(FxDbgErrorCode.InvalidSessionState, "Continue and step require a stopped session.");
            lock (observation.Gate)
            {
                RequireActive(observation);
                observation.LastSnapshot = (JObject)state.DeepClone();
                operation.AfterSequence = (long)state["eventSequence"]!;
            }
            await engine.InvokeAsync(observation.Id, command, arguments, Remaining(operation, timeout), token).ConfigureAwait(false);
            lock (observation.Gate) operation.Watchdog = WatchDeadlineAsync(observation, operation, timeout);
            if ((bool?)arguments["waitForStop"] == false)
            {
                // Establish the async acceptance boundary outside the observation lock.
                // Dispose waits for an in-flight cancellation callback before checking its token.
                cancellation.Dispose();
                token.ThrowIfCancellationRequested();
                lock (observation.Gate) return Envelope(observation.Id, operation.Snapshot());
            }
            JObject outcome;
            try { outcome = await operation.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                bool cancel;
                lock (observation.Gate) { cancel = !operation.IsCompleted; if (cancel) operation.Finishing = true; }
                if (cancel) await CancelSessionAsync(observation, operation).ConfigureAwait(false);
                outcome = await operation.Completion.Task.ConfigureAwait(false);
            }
            return OperationEnvelope(observation, outcome);
        }
        catch (Exception error)
        {
            string code = error is FxDbgException known ? FxDbgErrorCodeWireName.Format(known.Code) : error is OperationCanceledException ? "operation_cancelled" : "internal_error";
            if (token.IsCancellationRequested)
            {
                lock (observation.Gate)
                    if (operation.IsCompleted) return OperationEnvelope(observation, operation.Snapshot());
                await CancelSessionAsync(observation, operation).ConfigureAwait(false);
                lock (observation.Gate) return OperationEnvelope(observation, operation.Snapshot());
            }
            lock (observation.Gate)
            {
                operation.Finish(code == "operation_cancelled" ? "cancelled" : code == "operation_timed_out" ? "timedOut" : "failed", code: code, message: "Execution command did not complete; query session status before retrying.");
                PruneOperations(observation, DateTimeOffset.UtcNow);
            }
            throw;
        }
    }

    private static TimeSpan Remaining(ExecutionOperation operation, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - (DateTimeOffset.UtcNow - operation.CreatedAtUtc);
        if (remaining <= TimeSpan.Zero) throw new FxDbgException(FxDbgErrorCode.OperationTimedOut, "Execution deadline elapsed before command acceptance.");
        return remaining;
    }

    private async Task WatchDeadlineAsync(Observation observation, ExecutionOperation operation, TimeSpan timeout)
    {
        try
        {
            TimeSpan remaining = timeout - (DateTimeOffset.UtcNow - operation.CreatedAtUtc);
            if (remaining > TimeSpan.Zero)
            {
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                Task delay = Task.Delay(remaining, delayCancellation.Token);
                if (await Task.WhenAny(operation.Completion.Task, delay).ConfigureAwait(false) == operation.Completion.Task)
                {
                    delayCancellation.Cancel();
                    return;
                }
                await delay.ConfigureAwait(false);
            }
            lock (observation.Gate)
            {
                if (operation.IsCompleted || operation.Finishing) return;
                operation.Finishing = true;
            }
            var state = (JObject)await engine.InvokeAsync(observation.Id, "state", timeout: TimeSpan.FromSeconds(5), cancellationToken: lifetime.Token).ConfigureAwait(false);
            JToken? stop = state["stop"];
            if ((string?)state["target"]?["sessionState"] == "running")
                stop = await engine.InvokeAsync(observation.Id, "pause", timeout: TimeSpan.FromSeconds(5), cancellationToken: lifetime.Token).ConfigureAwait(false);
            lock (observation.Gate)
            {
                observation.LastStop = stop?.DeepClone();
                operation.Finish("timedOut", stop, "operation_timed_out", "Execution deadline elapsed; target is paused or exited. Query status before continuing.");
                PruneOperations(observation, DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (observation.Gate) observation.Closing = true;
            await Task.Run(() => engine.CloseSession(observation.Id)).ConfigureAwait(false);
            lock (observation.Gate)
            {
                MarkClosed(observation, "failed");
                operation.Finish("failed", code: "transport_disconnected", message: "Could not confirm a pause after timeout; the session was closed.");
                PruneOperations(observation, DateTimeOffset.UtcNow);
                observation.Closing = false;
            }
        }
    }

    private async Task CancelSessionAsync(Observation observation, ExecutionOperation operation)
    {
        lock (observation.Gate) observation.Closing = true;
        await Task.Run(() => engine.CloseSession(observation.Id)).ConfigureAwait(false);
        lock (observation.Gate)
        {
            MarkClosed(observation, "terminated");
            operation.Finish("cancelled", code: "operation_cancelled", message: "Request cancelled; session closed and Engine attempted safe detach. Check target state before reattaching.");
            PruneOperations(observation, DateTimeOffset.UtcNow);
            observation.Closing = false;
        }
    }

    private async Task<JToken> ControlAsync(Observation observation, string method, JObject arguments, TimeSpan timeout, CancellationToken token)
    {
        ExecutionOperation? controlledOperation;
        long beforeSequence;
        lock (observation.Gate)
        {
            RequireActive(observation);
            if (method == "terminate" && (bool?)observation.Target?["launchedByDebugger"] != true)
                throw Invalid("An attached target cannot be terminated. Use detach instead.");
            if (method is "detach" or "terminate") observation.Closing = true;
            controlledOperation = observation.ActiveOperation;
            beforeSequence = observation.Sequence;
        }
        try
        {
            JToken result = await engine.InvokeAsync(observation.Id, method, arguments, timeout, token).ConfigureAwait(false);
            if (method == "pause")
            {
                lock (observation.Gate)
                {
                    if (observation.Sequence == beforeSequence) observation.LastStop = result.DeepClone();
                    if (ReferenceEquals(observation.ActiveOperation, controlledOperation) && controlledOperation is { Finishing: false })
                        controlledOperation.Finish("completed", result);
                    PruneOperations(observation, DateTimeOffset.UtcNow);
                }
            }
            else
            {
                JToken? stop = null;
                if (method == "terminate")
                {
                    var exited = await engine.InvokeAsync(observation.Id, "state", timeout: timeout, cancellationToken: token).ConfigureAwait(false);
                    stop = exited["stop"]?.DeepClone();
                    await Task.Run(() => engine.CloseSession(observation.Id)).ConfigureAwait(false);
                }
                lock (observation.Gate)
                {
                    observation.Target = (JObject)result.DeepClone();
                    if (stop is not null) observation.LastStop = stop;
                    MarkClosed(observation, "terminated");
                    observation.ActiveOperation?.Finish(method == "terminate" ? "completed" : "cancelled", stop,
                        method == "detach" ? "operation_cancelled" : null, method == "detach" ? "Session detached." : null);
                    PruneOperations(observation, DateTimeOffset.UtcNow);
                }
            }
            return Envelope(observation.Id, result);
        }
        finally { if (method is "detach" or "terminate") lock (observation.Gate) observation.Closing = false; }
    }

    private static JObject OperationEnvelope(Observation observation, JObject operation)
    {
        JObject envelope = Envelope(observation.Id, operation);
        if (operation["error"] is JToken error)
        {
            envelope["ok"] = false;
            envelope["error"] = error.DeepClone();
        }
        return envelope;
    }

    private static void RequireActive(Observation observation)
    {
        // State RPCs and event draining run independently. Once a terminal state
        // has been returned, stale-handle requests must not race Engine shutdown
        // while the exit event is still waiting for the event pump.
        if (observation.ClosedAtUtc.HasValue || observation.Closing ||
            (string?)observation.Target?["sessionState"] is "terminated" or "failed" ||
            (string?)observation.LastSnapshot?["target"]?["sessionState"] is "terminated" or "failed")
            throw new FxDbgException(FxDbgErrorCode.InvalidSessionState, "Session is closing or ended.");
    }

    private static void MarkClosed(Observation observation, string state)
    {
        observation.ClosedAtUtc ??= DateTimeOffset.UtcNow;
        if (observation.Target is not null) observation.Target["sessionState"] = state;
    }

    private static void PruneOperations(Observation observation, DateTimeOffset now)
    {
        OperationRetention.Trim(observation.Operations, now, Retention);
    }
}
