using System;
using System.Linq;
using FxDbg.Core.Errors;
using FxDbg.Core.Events;
using FxDbg.Core.Sessions;
using Xunit;

namespace FxDbg.UnitTests.Sessions;

public sealed class DebugSessionTests
{
    [Fact]
    public void Start_moves_created_session_to_starting_and_records_event()
    {
        var session = new DebugSession(SessionId.New());

        SessionStateChangedEvent changed = session.Start();

        Assert.Equal(DebugSessionState.Starting, session.State);
        Assert.Equal(DebugSessionState.Created, changed.PreviousState);
        Assert.Equal(DebugSessionState.Starting, changed.CurrentState);
        Assert.Equal(new long[] { 1, 2 }, session.Events.Select(item => item.Sequence));
    }

    [Fact]
    public void Start_rejects_a_session_that_has_already_started_without_recording_an_event()
    {
        var session = new DebugSession(SessionId.New());
        session.Start();

        FxDbgException error = Assert.Throws<FxDbgException>(() => session.Start());

        Assert.Equal(FxDbgErrorCode.InvalidSessionState, error.Code);
        Assert.Equal(DebugSessionState.Starting, session.State);
        Assert.Equal(2, session.Events.Count);
    }

    [Fact]
    public void Resume_rejects_a_session_that_is_not_stopped()
    {
        var session = new DebugSession(SessionId.New());
        session.Start();

        FxDbgException error = Assert.Throws<FxDbgException>(() => session.Resume());

        Assert.Equal(FxDbgErrorCode.InvalidSessionState, error.Code);
        Assert.Equal(DebugSessionState.Starting, session.State);
        Assert.Equal(2, session.Events.Count);
    }

    [Fact]
    public void Lifecycle_records_every_state_change_in_order()
    {
        var session = new DebugSession(SessionId.New());
        session.Start();
        session.MarkRunning("process_started");
        session.MarkStopped(new StopInfo(StopReason.Breakpoint, 42, 7, "DefaultDomain", null));
        session.Resume();
        session.BeginDetach();
        session.MarkTerminated("detached");

        Assert.Equal(DebugSessionState.Terminated, session.State);
        Assert.Equal(
            new[]
            {
                DebugSessionState.Created,
                DebugSessionState.Starting,
                DebugSessionState.Running,
                DebugSessionState.Stopped,
                DebugSessionState.Running,
                DebugSessionState.Detaching,
                DebugSessionState.Terminated
            },
            session.Events.OfType<SessionStateChangedEvent>().Select(item => item.CurrentState));
        Assert.Equal(Enumerable.Range(1, session.Events.Count).Select(value => (long)value), session.Events.Select(item => item.Sequence));
    }

    [Fact]
    public void Resume_consumes_each_stop_exactly_once()
    {
        var session = new DebugSession(SessionId.New());
        session.Start();
        session.MarkRunning("process_started");
        session.MarkStopped(new StopInfo(StopReason.UserPause, 42, 7, "DefaultDomain", null));
        session.Resume();

        FxDbgException error = Assert.Throws<FxDbgException>(() => session.Resume());

        Assert.Equal(FxDbgErrorCode.InvalidSessionState, error.Code);
        Assert.Equal(DebugSessionState.Running, session.State);
    }

    [Fact]
    public void Session_rejects_a_missing_identity()
    {
        Assert.Throws<ArgumentNullException>(() => new DebugSession(null!));
    }

    [Fact]
    public void State_change_is_atomic_when_the_reason_is_invalid()
    {
        var session = new DebugSession(SessionId.New());
        session.Start();

        FxDbgException error = Assert.Throws<FxDbgException>(() => session.MarkRunning(null!));

        Assert.Equal(FxDbgErrorCode.InvalidRequest, error.Code);
        Assert.Equal(DebugSessionState.Starting, session.State);
        Assert.Equal(2, session.Events.Count);
    }
}
