using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using FxDbg.Host.Architecture;
using Newtonsoft.Json.Linq;

namespace FxDbg.Host.Engine;

public sealed class EngineProcessHost : IDisposable
{
    private readonly ArchitectureRouter router;
    private readonly EngineProcessPaths paths;
    private readonly object gate = new();
    private readonly Dictionary<SessionId, EngineConnection> connections = new();
    private readonly HashSet<SessionId> starting = new();
    private readonly Dictionary<SessionId, FxDbgErrorCode> failures = new();
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;

    public EngineProcessHost(ArchitectureRouter router, EngineProcessPaths paths)
    {
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Task<DebugTargetInfo> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        TargetArchitecture architecture = router.Resolve(request);
        var arguments = CommonArguments("launch", request.SessionId, architecture, request.Timeout);
        arguments.AddRange(new[] { "--exe", request.ExecutablePath });
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory)) arguments.AddRange(new[] { "--working-directory", request.WorkingDirectory! });
        foreach (KeyValuePair<string, string> pair in request.Environment) arguments.AddRange(new[] { "--env", pair.Key + "=" + pair.Value });
        foreach (string argument in request.Arguments) arguments.AddRange(new[] { "--arg", argument });
        if (request.StopAtEntry) arguments.Add("--stop-at-entry");
        return RunAsync(paths.For(architecture), arguments, request.SessionId, architecture, request.Timeout, cancellationToken);
    }

    public Task<DebugTargetInfo> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        TargetArchitecture architecture = router.Resolve(request);
        var arguments = CommonArguments("attach", request.SessionId, architecture, request.Timeout);
        arguments.AddRange(new[] { "--pid", request.ProcessId.ToString(CultureInfo.InvariantCulture) });
        return RunAsync(paths.For(architecture), arguments, request.SessionId, architecture, request.Timeout, cancellationToken);
    }

    public async Task<JToken> InvokeAsync(SessionId sessionId, string method, JObject? arguments = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (method == "start" || method.StartsWith("$", StringComparison.Ordinal)) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Reserved Engine command.");
        EngineConnection connection = Find(sessionId);
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(4)) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Command timeout must be positive and at most four minutes.");
        JObject parameters = arguments is null ? new JObject() : (JObject)arguments.DeepClone();
        parameters["commandTimeoutMs"] = (int)Math.Ceiling(limit.TotalMilliseconds);
        try
        {
            JToken result = await connection.Peer.CallAsync(method, parameters, limit + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (method == "detach") Release(sessionId, connection, null);
            return result;
        }
        catch (FxDbgException error)
        {
            if (error.Data.Contains("rpcClientDeadline") || error.Code == FxDbgErrorCode.TransportDisconnected)
            {
                FxDbgErrorCode code = connection.HasExited ? FxDbgErrorCode.EngineExited : error.Code;
                Release(sessionId, connection, code);
                if (code != error.Code) throw new FxDbgException(code, "Engine process exited unexpectedly.", error);
            }
            throw;
        }
    }

    public IReadOnlyList<JObject> DrainEvents(SessionId sessionId) => Find(sessionId).DrainEvents();
    public int GetEngineProcessId(SessionId sessionId) => Find(sessionId).ProcessId;
    public void CloseSession(SessionId sessionId)
    {
        EngineConnection? connection;
        lock (gate) connections.TryGetValue(sessionId, out connection);
        if (connection is not null) Release(sessionId, connection, null);
    }
    public IReadOnlyList<SessionId> ActiveSessions { get { lock (gate) return connections.Where(pair => pair.Value.Peer.IsConnected).Select(pair => pair.Key).ToArray(); } }

    public void Dispose()
    {
        EngineConnection[] active;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            active = connections.Values.ToArray();
            connections.Clear();
        }
        shutdown.Cancel();
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        Task.WhenAll(active.Select(connection => Task.Run(connection.Dispose))).GetAwaiter().GetResult();
        while (DateTime.UtcNow < deadline)
        {
            lock (gate) if (starting.Count == 0) break;
            Thread.Sleep(10);
        }
    }

    private EngineConnection Find(SessionId id)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(EngineProcessHost));
            if (connections.TryGetValue(id, out EngineConnection? connection)) return connection;
            throw new FxDbgException(failures.TryGetValue(id, out FxDbgErrorCode code) ? code : FxDbgErrorCode.SessionNotFound, "Session is no longer active.");
        }
    }

    private static List<string> CommonArguments(string mode, SessionId id, TargetArchitecture architecture, TimeSpan timeout) => new()
    {
        mode, "--session-id", id.ToString(), "--arch", TargetArchitectureWireName.Format(architecture),
        "--timeout-ms", Math.Ceiling(timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
    };

    private async Task<DebugTargetInfo> RunAsync(string enginePath, IReadOnlyList<string> arguments, SessionId id,
        TargetArchitecture architecture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(EngineProcessHost));
            if (connections.ContainsKey(id) || !starting.Add(id)) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Session already has an Engine.");
            failures.Remove(id);
        }
        EngineConnection? connection = null;
        Process? process = null;
        NamedPipeServerStream? pipe = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        deadline.CancelAfter(timeout + TimeSpan.FromSeconds(3));
        try
        {
            string pipeName = "fxdbg-" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var startInfo = new ProcessStartInfo
            {
                FileName = enginePath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(enginePath)!,
                ArgumentList = { "serve", pipeName, id.ToString() }
            };
            process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new FxDbgException(FxDbgErrorCode.EngineExited, "Engine process failed to start.");
            _ = DrainOutput(process.StandardOutput);
            _ = DrainOutput(process.StandardError);
            Task connected = pipe.WaitForConnectionAsync(deadline.Token);
            Task exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) != connected)
            {
                deadline.Token.ThrowIfCancellationRequested();
                throw new FxDbgException(FxDbgErrorCode.EngineExited, "Engine exited before connecting.");
            }
            await connected.ConfigureAwait(false);
            LocalPipeIdentity.RequireEngine(pipe, process.Id);
            connection = new EngineConnection(process, pipe, id.ToString());
            JObject start = new() { ["protocolVersion"] = WireJson.ProtocolVersion, ["arguments"] = new JArray(arguments), ["commandTimeoutMs"] = (int)Math.Ceiling(timeout.TotalMilliseconds) };
            JToken response = await connection.Peer.CallAsync("start", start, timeout + TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
            if ((int?)response["protocolVersion"] != WireJson.ProtocolVersion) throw new FxDbgException(FxDbgErrorCode.TransportDisconnected, "Engine protocol version mismatch.");
            DebugTargetInfo target = response["target"]!.ToObject<DebugTargetInfo>(WireJson.CreateSerializer())!;
            if (target.Architecture != architecture) throw new FxDbgException(FxDbgErrorCode.ArchitectureMismatch, "Engine reported an unexpected architecture.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(EngineProcessHost));
                connections.Add(id, connection);
            }
            EngineConnection captured = connection;
            _ = Watch(id, captured);
            connection = null;
            process = null;
            pipe = null;
            return target;
        }
        catch (OperationCanceledException error)
        {
            throw new FxDbgException(cancellationToken.IsCancellationRequested ? FxDbgErrorCode.OperationCancelled : FxDbgErrorCode.OperationTimedOut, "Engine startup cancelled or timed out.", error);
        }
        finally
        {
            lock (gate) starting.Remove(id);
            connection?.Dispose();
            pipe?.Dispose();
            if (process is not null && connection is null) { StopEngine(process); process.Dispose(); }
        }
    }

    private async Task Watch(SessionId id, EngineConnection connection)
    {
        await connection.Peer.Completion.ConfigureAwait(false);
        FxDbgErrorCode code;
        try { code = connection.HasExited ? FxDbgErrorCode.EngineExited : FxDbgErrorCode.TransportDisconnected; }
        catch (InvalidOperationException) { return; }
        Release(id, connection, code);
    }

    private void Release(SessionId id, EngineConnection connection, FxDbgErrorCode? failure)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(id, out EngineConnection? current) || !ReferenceEquals(current, connection)) return;
            connections.Remove(id);
            if (failure is not null)
            {
                if (failures.Count >= 1024) failures.Remove(failures.Keys.First());
                failures[id] = failure.Value;
            }
        }
        connection.Dispose();
    }

    private static async Task DrainOutput(StreamReader reader)
    {
        try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private static void StopEngine(Process process)
    {
        try { if (!process.WaitForExit(6000)) { process.Kill(); process.WaitForExit(2000); } }
        catch (InvalidOperationException) { }
    }

    private sealed class EngineConnection : IDisposable
    {
        private readonly object eventsGate = new();
        private readonly Queue<JObject> events = new();
        private int disposed;
        internal EngineConnection(Process process, Stream stream, string id)
        {
            Process = process;
            ProcessId = process.Id;
            Peer = new RpcPeer(stream, id, notification: (method, data) =>
            {
                if (method != "event") return;
                lock (eventsGate)
                {
                    if (events.Count == 2048) throw new InvalidDataException("Host event buffer overflowed.");
                    events.Enqueue((JObject)data.DeepClone());
                }
            });
        }
        internal Process Process { get; }
        internal int ProcessId { get; }
        internal bool HasExited { get { try { return Process.HasExited; } catch (InvalidOperationException) { return true; } } }
        internal RpcPeer Peer { get; }
        internal IReadOnlyList<JObject> DrainEvents()
        {
            lock (eventsGate) { JObject[] result = events.ToArray(); events.Clear(); return result; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Peer.Dispose();
            StopEngine(Process);
            Process.Dispose();
        }
    }
}
