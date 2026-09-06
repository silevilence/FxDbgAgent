using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
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

/// <summary>Keeps the shared Host alive between local CLI invocations.</summary>
public static class PersistentSessionServer
{
    public static string PipeName(SessionId id) => "fxdbg-cli-" + id;

    public static async Task RunAsync(SessionId id, EngineProcessPaths paths, CancellationToken cancellationToken)
    {
        using var host = new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()), paths);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        int lifecycle = 0; // 0 starting, 1 active, 2 explicit closing.
        Task monitor = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(500, lifetime.Token).ConfigureAwait(false);
                    if (Volatile.Read(ref lifecycle) == 1 && host.ActiveSessions.Count == 0) lifetime.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(PipeName(id), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                LocalPipeIdentity.RequireEngine(pipe, null);
                lifetime.CancelAfter(TimeSpan.FromMinutes(30));
                using var peer = new RpcPeer(pipe, id.ToString(), async (method, parameters, token) =>
                {
                    int milliseconds = (int?)parameters["commandTimeoutMs"] ?? 10000;
                    if (milliseconds <= 0 || milliseconds > 240000) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Command timeout is out of range.");
                    TimeSpan timeout = TimeSpan.FromMilliseconds(milliseconds);
                    if (method is "launch" or "attach")
                    {
                        if (Volatile.Read(ref lifecycle) != 0) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Session already started.");
                        TargetArchitecture architecture = Enum.TryParse((string?)parameters["architecture"] ?? "auto", true, out TargetArchitecture value) ? value : throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Invalid target architecture.");
                        var target = method == "launch"
                            ? await host.LaunchAsync(new LaunchRequest(id, (string)parameters["executablePath"]!, parameters["arguments"]?.ToObject<string[]>(),
                                (string?)parameters["workingDirectory"], parameters["environment"]?.ToObject<Dictionary<string, string>>(), architecture,
                                (bool?)parameters["stopAtEntry"] ?? false, timeout, parameters["sourceMappings"]?.ToObject<SourcePathMapping[]>()), token).ConfigureAwait(false)
                            : await host.AttachAsync(new AttachRequest(id, (int)parameters["processId"]!, architecture, timeout, parameters["sourceMappings"]?.ToObject<SourcePathMapping[]>()), token).ConfigureAwait(false);
                        Volatile.Write(ref lifecycle, 1);
                        return new JObject { ["target"] = WireJson.Value(target), ["hostProcessId"] = Environment.ProcessId, ["engineProcessId"] = host.GetEngineProcessId(id) };
                    }
                    if (method == "events") return WireJson.Value(host.DrainEvents(id));
                    if (method == "detach") Volatile.Write(ref lifecycle, 2);
                    return await host.InvokeAsync(id, method, parameters, timeout, token).ConfigureAwait(false);
                });
                using CancellationTokenRegistration close = lifetime.Token.Register(peer.Dispose);
                await peer.Completion.ConfigureAwait(false);
                if (Volatile.Read(ref lifecycle) != 1 || host.ActiveSessions.Count == 0) break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lifetime.Cancel(); await monitor.ConfigureAwait(false); }
    }
}
