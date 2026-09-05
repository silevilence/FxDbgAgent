using System;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;
using FxDbg.Host.Architecture;
using FxDbg.Host.Engine;
using FxDbg.Host.Sessions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class CallAdmissionTests
{
    [Fact]
    public async System.Threading.Tasks.Task Cancelled_status_keeps_its_slot_until_the_underlying_query_finishes()
    {
        var admission = new CallAdmission(new SessionServiceLimits(controlCalls: 1));
        var query = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = admission.Enter("status", CancellationToken.None);
        lease.HoldUntil(query.Task);
        lease.Dispose();
        Assert.Throws<FxDbgException>(() => admission.Enter("status", CancellationToken.None));
        query.SetResult();
        // Deferred release runs asynchronously; wait for the capacity without admitting unbounded work.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            try { using var next = admission.Enter("status", CancellationToken.None); break; }
            catch (FxDbgException) { await System.Threading.Tasks.Task.Delay(1, timeout.Token); }
        }
    }
    [Fact]
    public void SaturatedOrdinaryCallsKeepBoundedControlCapacityAndReleaseExactlyOnce()
    {
        var admission = new CallAdmission(new SessionServiceLimits(1, 1, 1));
        var ordinary = admission.Enter("continue", CancellationToken.None);
        Assert.Equal(FxDbgErrorCode.RateLimited, Assert.Throws<FxDbgException>(() => admission.Enter("launch", CancellationToken.None)).Code);
        using (admission.Enter("pause", CancellationToken.None))
            Assert.Equal(1000, Assert.Throws<FxDbgException>(() => admission.Enter("status", CancellationToken.None)).Data["retryAfterMs"]);
        ordinary.Dispose(); ordinary.Dispose();
        using var retry = admission.Enter("attach", CancellationToken.None);
        using var control = admission.Enter("detach", CancellationToken.None);
    }

    [Fact]
    public void PreCancelledCallConsumesNoCapacity()
    {
        var admission = new CallAdmission(new SessionServiceLimits(1, 1, 1));
        Assert.Throws<OperationCanceledException>(() => admission.Enter("launch", new CancellationToken(true)));
        using var available = admission.Enter("launch", CancellationToken.None);
        Assert.Throws<ArgumentException>(() => new SessionServiceLimits(9));
        Assert.Throws<ArgumentException>(() => new SessionServiceLimits(1, 0));
        Assert.Throws<ArgumentException>(() => new SessionServiceLimits(1, 1, 9));
    }

    [Fact]
    public async Task PreCancelledLaunchNeverStartsAnEngine()
    {
        using var engine = new EngineProcessHost(new ArchitectureRouter(new PeArchitectureDetector(), new ProcessArchitectureDetector()),
            new EngineProcessPaths("C:/missing-engine-x86.exe", "C:/missing-engine-x64.exe"));
        await using var service = new DebugSessionService(engine);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InvokeAsync("launch", new JObject { ["exe"] = "C:/missing-target.exe" }, new CancellationToken(true)));
        Assert.Empty(engine.ActiveSessions);
    }

    [Fact]
    public void SessionRetentionReservesCapacityAndExpiresOnlyTerminalRecords()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionId old = SessionId.New(), recent = SessionId.New();
        var terminal = new[] { (old, now - TimeSpan.FromMinutes(9)), (recent, now) };
        Assert.Equal(old, Assert.Single(SessionRetention.Evictions(terminal, 1024, now, extraRecords: 1)));
        Assert.Empty(SessionRetention.Evictions(terminal, 100, now));
        Assert.Equal(old, Assert.Single(SessionRetention.Evictions(terminal, 100, now + TimeSpan.FromMinutes(1))));
        Assert.Equal(2, SessionRetention.Evictions(terminal, 100, now + TimeSpan.FromMinutes(10)).Length);
    }
}
