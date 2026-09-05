using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Model;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using FxDbg.Host.Engine;
using FxDbg.Host.Sessions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class SessionRaceTests
{
    [Fact]
    public async Task Late_pause_response_cannot_complete_next_run_or_remove_its_deadline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var engine = new OrderedEngine();
        await using var service = new DebugSessionService(engine);
        string session = (string)(await service.InvokeAsync("launch", new JObject { ["exe"] = "fixture.exe" }))["sessionId"]!;
        JObject Args() => new() { ["sessionId"] = session, ["timeoutMs"] = 1000 };
        Task<JToken> first = service.InvokeAsync("continue", Args(), timeout.Token);
        await engine.Continued.Task.WaitAsync(timeout.Token);
        Task<JToken> pause = service.InvokeAsync("pause", Args(), timeout.Token);
        Assert.Equal("completed", (string?)(await first.WaitAsync(timeout.Token))["result"]?["state"]);
        JObject next = Args(); next["waitForStop"] = false; next["timeoutMs"] = 150;
        JToken second = await service.InvokeAsync("continue", next, timeout.Token);
        string operation = (string)second["result"]!["operationId"]!;
        engine.PauseResponse.TrySetResult();
        await pause.WaitAsync(timeout.Token);
        JObject query = Args(); query["operationId"] = operation;
        JToken status = await service.InvokeAsync("status", query, timeout.Token);
        Assert.Equal("running", (string?)status["result"]?["operation"]?["state"]);
        do
        {
            await Task.Delay(10, timeout.Token);
            status = await service.InvokeAsync("status", query, timeout.Token);
        } while ((string?)status["result"]?["operation"]?["state"] == "running");
        Assert.Equal("timedOut", (string?)status["result"]?["operation"]?["state"]);
        Assert.Equal("stopped", engine.State);
    }

    [Fact]
    public async Task Cancellation_after_resume_before_async_acceptance_closes_instead_of_sticking()
    {
        using var cancelled = new CancellationTokenSource();
        using var engine = new OrderedEngine { AfterResume = cancelled.Cancel };
        await using var service = new DebugSessionService(engine);
        string session = (string)(await service.InvokeAsync("launch", new JObject { ["exe"] = "fixture.exe" }))["sessionId"]!;
        JToken result = await service.InvokeAsync("continue", new JObject { ["sessionId"] = session, ["waitForStop"] = false }, cancelled.Token);
        Assert.True(engine.Closed);
        Assert.Equal("cancelled", (string?)result["result"]?["state"]);
        JToken status = await service.InvokeAsync("status", new JObject { ["sessionId"] = session });
        Assert.False((bool)status["result"]!["closing"]!);
        Assert.Equal("terminated", (string?)status["result"]?["target"]?["sessionState"]);
    }

    // Only unit tests use this controlled boundary. All acceptance suites still launch real stdio/Engine processes.
    [Fact]
    public async Task Timeout_winning_cancellation_race_does_not_leave_session_closing()
    {
        using var cancelled = new CancellationTokenSource();
        using var engine = new OrderedEngine { AfterResume = () => Thread.Sleep(100), AfterPause = cancelled.Cancel };
        engine.PauseResponse.SetResult();
        await using var service = new DebugSessionService(engine);
        string session = (string)(await service.InvokeAsync("launch", new JObject { ["exe"] = "fixture.exe" }))["sessionId"]!;
        JToken result = await service.InvokeAsync("continue", new JObject { ["sessionId"] = session, ["waitForStop"] = false, ["timeoutMs"] = 50 }, cancelled.Token);
        Assert.Equal("timedOut", (string?)result["result"]?["state"]);
        JToken status = await service.InvokeAsync("status", new JObject { ["sessionId"] = session });
        Assert.NotEqual(true, (bool?)status["result"]?["closing"]);
        Assert.False(engine.Closed);
        Assert.Equal("stopped", engine.State);
    }

    private sealed class OrderedEngine : IEngineSessionHost
    {
        private readonly ConcurrentQueue<JObject> events = new();
        private long sequence;
        private int pauses;
        internal string State = "stopped";
        internal bool Closed;
        internal Action? AfterResume;
        internal Action? AfterPause;
        internal TaskCompletionSource Continued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PauseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private JObject? stop;
        private DebugTargetInfo Target => new(123, TargetArchitecture.X86, "v4.0.30319", true,
            State == "running" ? DebugSessionState.Running : DebugSessionState.Stopped);
        public Task<DebugTargetInfo> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken) => Task.FromResult(Target);
        public Task<DebugTargetInfo> AttachAsync(AttachRequest request, CancellationToken cancellationToken) => Task.FromResult(Target);
        public async Task<JToken> InvokeAsync(SessionId sessionId, string method, JObject? arguments = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (method == "state") return new JObject { ["target"] = WireJson.Value(Target), ["stop"] = stop?.DeepClone(), ["eventSequence"] = sequence };
            if (method == "continue")
            {
                State = "running"; Continued.TrySetResult(); AfterResume?.Invoke();
                return WireJson.Value(Target);
            }
            if (method == "pause")
            {
                State = "stopped";
                var paused = new JObject { ["reason"] = "userPause", ["processId"] = 123, ["threadId"] = 1 };
                stop = paused;
                events.Enqueue(new JObject { ["sequence"] = Interlocked.Increment(ref sequence), ["kind"] = "stopped", ["data"] = new JObject { ["stop"] = paused.DeepClone() } });
                if (Interlocked.Increment(ref pauses) == 1) await PauseResponse.Task.WaitAsync(cancellationToken);
                AfterPause?.Invoke();
                return paused;
            }
            throw new InvalidOperationException(method);
        }
        public IReadOnlyList<JObject> DrainEvents(SessionId sessionId)
        {
            var result = new List<JObject>();
            while (events.TryDequeue(out JObject? item)) result.Add(item);
            return result;
        }
        public void CloseSession(SessionId sessionId) => Closed = true;
        public void Dispose() => PauseResponse.TrySetResult();
    }
}
