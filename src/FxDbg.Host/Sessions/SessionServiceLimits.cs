using System;
using System.Threading;
using FxDbg.Core.Errors;

namespace FxDbg.Host.Sessions;

public sealed class SessionServiceLimits
{
    public SessionServiceLimits(int activeSessions = 8, int ordinaryCalls = 32, int controlCalls = 8)
    {
        if (activeSessions is < 1 or > 8 || ordinaryCalls is < 1 or > 32 || controlCalls is < 1 or > 8)
            throw new ArgumentException("Limits must be 1-8 sessions, 1-32 ordinary calls and 1-8 control calls.");
        ActiveSessions = activeSessions; OrdinaryCalls = ordinaryCalls; ControlCalls = controlCalls;
    }
    public int ActiveSessions { get; }
    public int OrdinaryCalls { get; }
    public int ControlCalls { get; }
}

internal sealed class CallAdmission
{
    private readonly SemaphoreSlim ordinary;
    private readonly SemaphoreSlim control;
    internal CallAdmission(SessionServiceLimits limits)
    {
        ordinary = new(limits.OrdinaryCalls, limits.OrdinaryCalls);
        control = new(limits.ControlCalls, limits.ControlCalls);
    }
    internal IDisposable Enter(string method, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SemaphoreSlim capacity = method is "status" or "pause" or "detach" or "terminate" ? control : ordinary;
        if (!capacity.Wait(0)) throw RateLimited();
        return new Lease(capacity);
    }
    internal static FxDbgException RateLimited()
    {
        var error = new FxDbgException(FxDbgErrorCode.RateLimited, "Host capacity is full; retry after 1000 milliseconds.");
        error.Data["retryAfterMs"] = 1000;
        return error;
    }
    private sealed class Lease(SemaphoreSlim capacity) : IDisposable
    {
        private SemaphoreSlim? held = capacity;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
