using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Core.Execution;
using FxDbg.Engine.Scheduling;
using Xunit;

namespace FxDbg.UnitTests.Engine;

public sealed class SingleThreadCommandSchedulerTests
{
    [Fact]
    public async Task Commands_run_serially_on_one_dedicated_thread()
    {
        using var scheduler = new SingleThreadCommandScheduler("test-scheduler");
        var threadIds = new ConcurrentBag<int>();
        int activeCommands = 0;
        int maximumConcurrency = 0;

        Task<int>[] commands = Enumerable.Range(0, 32)
            .Select(index => scheduler.EnqueueAsync(
                cancellationToken =>
                {
                    int active = Interlocked.Increment(ref activeCommands);
                    maximumConcurrency = Math.Max(maximumConcurrency, active);
                    threadIds.Add(Environment.CurrentManagedThreadId);
                    Thread.SpinWait(10_000);
                    Interlocked.Decrement(ref activeCommands);
                    return index;
                },
                TimeSpan.FromSeconds(2)))
            .ToArray();

        int[] results = await Task.WhenAll(commands);

        Assert.Equal(Enumerable.Range(0, 32), results);
        Assert.Equal(1, maximumConcurrency);
        Assert.Single(threadIds.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadIds.Distinct().Single());
    }

    [Fact]
    public async Task One_hundred_pause_continue_cycles_are_exactly_paired()
    {
        using var scheduler = new SingleThreadCommandScheduler("pairing-test");
        var pairing = new ContinueStopCoordinator(TargetExecutionState.Running);
        int nativeContinueCalls = 0;

        for (long sequence = 1; sequence <= 100; sequence++)
        {
            long capturedSequence = sequence;
            await scheduler.EnqueueAsync(
                cancellationToken =>
                {
                    pairing.RecordCallbackStop(capturedSequence);
                    pairing.Continue(() => nativeContinueCalls++);
                    return true;
                },
                TimeSpan.FromSeconds(1));
        }

        Assert.Equal(TargetExecutionState.Running, pairing.State);
        Assert.Equal(100, pairing.StopCount);
        Assert.Equal(100, pairing.ContinueCount);
        Assert.Equal(100, nativeContinueCalls);
        Assert.Equal(0, pairing.OutstandingStopCount);
    }

    [Fact]
    public void Duplicate_continue_is_rejected_without_calling_the_target()
    {
        var pairing = new ContinueStopCoordinator(TargetExecutionState.Stopped);
        int nativeContinueCalls = 0;

        pairing.Continue(() => nativeContinueCalls++);
        FxDbgException error = Assert.Throws<FxDbgException>(
            () => pairing.Continue(() => nativeContinueCalls++));

        Assert.Equal(FxDbgErrorCode.InvalidSessionState, error.Code);
        Assert.Equal(TargetExecutionState.Running, pairing.State);
        Assert.Equal(1, nativeContinueCalls);
    }

    [Fact]
    public void Failed_native_continue_preserves_the_outstanding_stop()
    {
        var pairing = new ContinueStopCoordinator(TargetExecutionState.Stopped);

        Assert.Throws<InvalidOperationException>(
            () => pairing.Continue(() => throw new InvalidOperationException("native failure")));

        Assert.Equal(TargetExecutionState.Stopped, pairing.State);
        Assert.Equal(1, pairing.OutstandingStopCount);
        Assert.Equal(0, pairing.ContinueCount);
    }

    [Fact]
    public async Task Queued_command_cancellation_is_prompt_and_the_command_never_runs()
    {
        using var scheduler = new SingleThreadCommandScheduler("cancellation-test");
        using var commandStarted = new ManualResetEventSlim(false);
        using var releaseCommand = new ManualResetEventSlim(false);
        Task<bool> blocking = scheduler.EnqueueAsync(
            cancellationToken =>
            {
                commandStarted.Set();
                releaseCommand.Wait(cancellationToken);
                return true;
            },
            TimeSpan.FromSeconds(2));
        Assert.True(commandStarted.Wait(TimeSpan.FromSeconds(1)));

        using var cancellation = new CancellationTokenSource();
        bool ran = false;
        Task<bool> queued = scheduler.EnqueueAsync(
            cancellationToken =>
            {
                ran = true;
                return true;
            },
            TimeSpan.FromSeconds(2),
            cancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        FxDbgException error = await Assert.ThrowsAsync<FxDbgException>(() => queued);
        releaseCommand.Set();
        await blocking;

        Assert.Equal(FxDbgErrorCode.OperationCancelled, error.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(ran);
    }

    [Fact]
    public async Task Running_command_timeout_is_cooperative_and_scheduler_remains_consistent()
    {
        using var scheduler = new SingleThreadCommandScheduler("timeout-test");

        Task<bool> timedOut = scheduler.EnqueueAsync<bool>(
            cancellationToken =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.SpinWait(1_000);
                }
            },
            TimeSpan.FromMilliseconds(100));

        FxDbgException error = await Assert.ThrowsAsync<FxDbgException>(() => timedOut);
        int next = await scheduler.EnqueueAsync(_ => 7, TimeSpan.FromSeconds(1));

        Assert.Equal(FxDbgErrorCode.OperationTimedOut, error.Code);
        Assert.Equal(7, next);
    }

    [Fact]
    public async Task Running_command_external_cancellation_is_cooperative_and_scheduler_remains_consistent()
    {
        using var scheduler = new SingleThreadCommandScheduler("running-cancellation-test");
        using var started = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        Task<bool> cancelled = scheduler.EnqueueAsync<bool>(
            cancellationToken =>
            {
                started.Set();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.SpinWait(1_000);
                }
            },
            TimeSpan.FromSeconds(2),
            cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(1)));

        cancellation.Cancel();
        FxDbgException error = await Assert.ThrowsAsync<FxDbgException>(() => cancelled);
        int next = await scheduler.EnqueueAsync(_ => 9, TimeSpan.FromSeconds(1));

        Assert.Equal(FxDbgErrorCode.OperationCancelled, error.Code);
        Assert.Equal(9, next);
    }

    [Fact]
    public async Task Successful_return_is_the_commit_point_even_when_timeout_arrives_during_the_side_effect()
    {
        using var scheduler = new SingleThreadCommandScheduler("commit-point-test");
        using var sideEffectStarted = new ManualResetEventSlim(false);
        using var releaseSideEffect = new ManualResetEventSlim(false);
        int sideEffects = 0;
        Task<int> command = scheduler.EnqueueAsync(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                sideEffectStarted.Set();
                releaseSideEffect.Wait();
                sideEffects++;
                return 17;
            },
            TimeSpan.FromMilliseconds(100));
        Assert.True(sideEffectStarted.Wait(TimeSpan.FromSeconds(1)));
        Thread.Sleep(150);
        releaseSideEffect.Set();

        int result = await command;

        Assert.Equal(17, result);
        Assert.Equal(1, sideEffects);
    }

    [Fact]
    public async Task Dispose_cancellation_can_reenter_enqueue_without_deadlocking()
    {
        var scheduler = new SingleThreadCommandScheduler("dispose-reentrancy-test");
        using var commandStarted = new ManualResetEventSlim(false);
        using var callbackRan = new ManualResetEventSlim(false);
        Task<bool>? reentrant = null;
        Task<bool> running = scheduler.EnqueueAsync(
            cancellationToken =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () =>
                    {
                        reentrant = scheduler.EnqueueAsync(_ => true, TimeSpan.FromSeconds(1));
                        callbackRan.Set();
                    });
                commandStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                return true;
            },
            TimeSpan.FromSeconds(5));
        Assert.True(commandStarted.Wait(TimeSpan.FromSeconds(1)));

        Task dispose = Task.Run(scheduler.Dispose);
        Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(dispose, completed);
        await dispose;
        Assert.True(await running);
        Assert.True(callbackRan.Wait(TimeSpan.FromSeconds(1)));
        Assert.NotNull(reentrant);
        FxDbgException error = await Assert.ThrowsAsync<FxDbgException>(() => reentrant!);
        Assert.Equal(FxDbgErrorCode.OperationCancelled, error.Code);
    }

    [Fact]
    public async Task Idle_work_failure_stops_the_scheduler_and_rejects_later_commands()
    {
        using var attempted = new ManualResetEventSlim(false);
        using var scheduler = new SingleThreadCommandScheduler(
            "failed-idle-test",
            _ =>
            {
                attempted.Set();
                throw new InvalidOperationException("callback pump failed");
            });
        Assert.True(attempted.Wait(TimeSpan.FromSeconds(1)));

        FxDbgException error = await Assert.ThrowsAsync<FxDbgException>(
            () => scheduler.EnqueueAsync(_ => true, TimeSpan.FromSeconds(1)));

        Assert.Equal(FxDbgErrorCode.EngineExited, error.Code);
    }
}
