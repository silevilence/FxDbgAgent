using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using Newtonsoft.Json.Linq;

namespace FxDbg.Engine.Protocol;

/// <summary>Bounded bidirectional JSON-RPC requests, responses and notifications over one stream.</summary>
public sealed class RpcPeer : IDisposable
{
    private readonly Stream stream;
    private readonly string sessionId;
    private readonly Func<string, JObject, CancellationToken, Task<JToken>>? handler;
    private readonly Action<string, JObject>? notification;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken>> pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();
    private readonly TaskCompletionSource<bool> disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastReceived = Stopwatch.GetTimestamp();
    private int closed;

    public RpcPeer(Stream stream, string sessionId,
        Func<string, JObject, CancellationToken, Task<JToken>>? handler = null,
        Action<string, JObject>? notification = null)
    {
        this.stream = stream;
        this.sessionId = sessionId;
        this.handler = handler;
        this.notification = notification;
        _ = Task.Run(ReadLoop);
        _ = Task.Run(HeartbeatLoop);
    }

    public Task Completion => disconnected.Task;
    public bool IsConnected => Volatile.Read(ref closed) == 0;

    public async Task<JToken> CallAsync(string method, JObject arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "RPC timeout must be positive and at most five minutes.");
        if (!IsConnected) throw Disconnected();
        if (pending.Count >= 128) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Too many pending RPC requests.");
        string id = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.TryAdd(id, result);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        deadline.CancelAfter(timeout);
        using CancellationTokenRegistration registration = deadline.Token.Register(() => result.TrySetException(
            !IsConnected ? Disconnected() : ClientDeadline(cancellationToken.IsCancellationRequested)));
        bool completed = false;
        try
        {
            JObject parameters = (JObject)arguments.DeepClone();
            parameters["sessionId"] = sessionId;
            parameters["timeoutMs"] = (int)Math.Ceiling(timeout.TotalMilliseconds);
            await Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
            JToken value = await result.Task.ConfigureAwait(false);
            completed = true;
            return value;
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (!completed && IsConnected && deadline.IsCancellationRequested)
                try { await NotifyAsync("$cancel", new JObject { ["requestId"] = id }).ConfigureAwait(false); } catch (FxDbgException) { }
        }
    }

    public Task NotifyAsync(string method, JObject arguments)
    {
        JObject parameters = (JObject)arguments.DeepClone();
        parameters["sessionId"] = sessionId;
        return Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
    }

    public void Dispose() => Close(Disconnected());

    private async Task Send(JObject message)
    {
        // Validate before writing any bytes: a large result is a request error, not a broken stream.
        byte[] bytes;
        try { bytes = RpcFrame.Encode(message); }
        catch (InvalidDataException error)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest,
                "RPC message exceeds 4 MiB. Reduce the request size, variable page count, depth, or string length and retry.", error);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        bool acquired = false;
        try
        {
            await writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            // Closing the stream is the fallback when a platform I/O ignores cancellation.
            using CancellationTokenRegistration cancel = timeout.Token.Register(() => Close(Disconnected()));
            await RpcFrame.WriteAsync(stream, bytes, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Close(error);
            throw Disconnected();
        }
        finally { if (acquired) writer.Release(); }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                JObject? message = await RpcFrame.ReadAsync(stream, shutdown.Token).ConfigureAwait(false);
                if (message is null) break;
                if ((string?)message["jsonrpc"] != "2.0") throw new InvalidDataException("Unsupported JSON-RPC version.");
                Interlocked.Exchange(ref lastReceived, Stopwatch.GetTimestamp());
                if (message["method"] is JValue methodValue && methodValue.Type == JTokenType.String)
                {
                    string method = (string)methodValue!;
                    JObject arguments = message["params"] as JObject ?? throw new InvalidDataException("RPC params must be an object.");
                    string? id = message["id"]?.Type == JTokenType.String ? (string?)message["id"] : null;
                    if ((string?)arguments["sessionId"] != sessionId)
                    {
                        if (id is null) throw new InvalidDataException("Notification session mismatch.");
                        await SendError(id, new FxDbgException(FxDbgErrorCode.SessionNotFound, "Session ID does not match this Engine.")).ConfigureAwait(false);
                        continue;
                    }
                    if (id is null)
                    {
                        if (message["id"] is not null) throw new InvalidDataException("RPC request IDs must be strings.");
                        if (method == "$cancel")
                        {
                            string requestId = (string?)arguments["requestId"] ?? "";
                            if (active.TryGetValue(requestId, out CancellationTokenSource? cancellation))
                                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                        }
                        else if (method != "$heartbeat") notification?.Invoke(method, arguments);
                        continue;
                    }
                    if (active.Count >= 128) throw new InvalidDataException("Too many active requests.");
                    var source = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    if (!active.TryAdd(id, source)) { source.Dispose(); throw new InvalidDataException("Duplicate active request ID."); }
                    _ = HandleRequest(id, method, arguments, source);
                }
                else
                {
                    string id = message["id"]?.Type == JTokenType.String ? (string)message["id"]! : throw new InvalidDataException("Response ID must be a string.");
                    bool hasResult = message.Property("result") is not null;
                    bool hasError = message.Property("error") is not null;
                    if (hasResult == hasError) throw new InvalidDataException("Response requires exactly one result or error.");
                    if (!pending.TryGetValue(id, out TaskCompletionSource<JToken>? result)) continue;
                    if (hasResult) result.TrySetResult(message["result"]!);
                    else
                    {
                        string? code = (string?)message["error"]?["data"]?["code"];
                        result.TrySetException(new FxDbgException(FxDbgErrorCodeWireName.TryParse(code ?? "", out FxDbgErrorCode parsed) ? parsed : FxDbgErrorCode.InternalError,
                            (string?)message["error"]?["message"] ?? "Engine request failed."));
                    }
                }
            }
        }
        catch (Exception error) { Close(error); }
        finally { Close(Disconnected()); }
    }

    private async Task HandleRequest(string id, string method, JObject arguments, CancellationTokenSource cancellation)
    {
        try
        {
            if (handler is null) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "This peer does not accept requests.");
            JToken result = await handler(method, arguments, cancellation.Token).ConfigureAwait(false);
            await Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (IsConnected) try { await SendError(id, error).ConfigureAwait(false); } catch (Exception) { }
        }
        finally { active.TryRemove(id, out _); cancellation.Dispose(); }
    }

    private Task SendError(string id, Exception error)
    {
        FxDbgErrorCode code = error is FxDbgException known ? known.Code : error is OperationCanceledException ? FxDbgErrorCode.OperationCancelled : FxDbgErrorCode.InternalError;
        return Send(new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new JObject { ["code"] = -32000, ["message"] = error is FxDbgException ? error.Message : "Engine operation failed.",
                ["data"] = new JObject { ["code"] = FxDbgErrorCodeWireName.Format(code) } }
        });
    }

    private async Task HeartbeatLoop()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(1000, shutdown.Token).ConfigureAwait(false);
                double seconds = (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastReceived)) / (double)Stopwatch.Frequency;
                if (seconds > 6) throw new TimeoutException("RPC heartbeat expired.");
                await NotifyAsync("$heartbeat", new JObject()).ConfigureAwait(false);
            }
        }
        catch (Exception error) { Close(error); }
    }

    private void Close(Exception error)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { shutdown.Cancel(); } catch (AggregateException) { }
        try { stream.Dispose(); } catch (IOException) { }
        foreach (TaskCompletionSource<JToken> result in pending.Values) result.TrySetException(Disconnected());
        disconnected.TrySetResult(true);
        GC.KeepAlive(error); // Never log frames or variable values while diagnosing a disconnect.
    }

    private static FxDbgException Disconnected() => new(FxDbgErrorCode.TransportDisconnected, "Engine transport disconnected.");

    private static FxDbgException ClientDeadline(bool cancelled)
    {
        var error = new FxDbgException(cancelled ? FxDbgErrorCode.OperationCancelled : FxDbgErrorCode.OperationTimedOut, "RPC request cancelled or timed out.");
        error.Data["rpcClientDeadline"] = true;
        return error;
    }
}
