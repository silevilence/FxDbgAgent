using System;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Engine.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FxDbg.UnitTests.Engine;

public sealed class RpcPeerTests
{
    [Fact]
    public async Task Oversized_response_returns_actionable_error_and_keeps_session_usable()
    {
        var (server, client) = await Pipes();
        using (server)
        using (client)
        using (var remote = new RpcPeer(server, "test", (method, args, token) => Task.FromResult<JToken>(
            new JValue(method == "large" ? new string('x', RpcFrame.MaximumBytes) : "ok"))))
        using (var local = new RpcPeer(client, "test"))
        {
            FxDbgException error = await Assert.ThrowsAsync<FxDbgException>(() => local.CallAsync("large", new JObject(), TimeSpan.FromSeconds(5)));
            Assert.Equal(FxDbgErrorCode.InvalidRequest, error.Code);
            Assert.Contains("4 MiB", error.Message);
            Assert.True(local.IsConnected && remote.IsConnected);
            Assert.Equal("ok", (string?)await local.CallAsync("echo", new JObject(), TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Concurrent_responses_correlate_and_cancel_reaches_the_remote_handler()
    {
        var (server, client) = await Pipes();
        using (server)
        using (client)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var remote = new RpcPeer(server, "test", async (method, args, token) =>
            {
                if (method == "wait")
                {
                    try { await Task.Delay(30000, token); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
                }
                await Task.Delay((int)args["value"]! % 5, token);
                return args["value"]!;
            });
            using var local = new RpcPeer(client, "test");
            Task<JToken>[] calls = Enumerable.Range(0, 24).Select(value => local.CallAsync("echo", new JObject { ["value"] = value }, TimeSpan.FromSeconds(5))).ToArray();
            JToken[] results = await Task.WhenAll(calls);
            Assert.Equal(Enumerable.Range(0, 24), results.Select(value => (int)value));
            using var cancellation = new CancellationTokenSource(100);
            FxDbgException failure = await Assert.ThrowsAsync<FxDbgException>(() => local.CallAsync("wait", new JObject(), TimeSpan.FromSeconds(5), cancellation.Token));
            Assert.Equal(FxDbgErrorCode.OperationCancelled, failure.Code);
            Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(99, (int)await local.CallAsync("echo", new JObject { ["value"] = 99 }, TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public async Task Heartbeats_keep_idle_peers_alive_and_silent_peer_expires()
    {
        var (server, client) = await Pipes();
        using (server)
        using (client)
        using (var remote = new RpcPeer(server, "test"))
        using (var local = new RpcPeer(client, "test"))
        {
            await Task.Delay(7100);
            Assert.True(local.IsConnected && remote.IsConnected);
        }
        var (silentServer, silentClient) = await Pipes();
        using (silentServer)
        using (silentClient)
        using (var peer = new RpcPeer(silentServer, "silent"))
        {
            // Drain outgoing traffic but never reply or heartbeat.
            Task drain = Task.Run(async () => { try { while (await RpcFrame.ReadAsync(silentClient, CancellationToken.None) is not null) { } } catch (System.IO.IOException) { } });
            await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(peer.IsConnected);
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<(NamedPipeServerStream, NamedPipeClientStream)> Pipes()
    {
        string name = "fxdbg-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(3000));
        return (server, client);
    }
}
