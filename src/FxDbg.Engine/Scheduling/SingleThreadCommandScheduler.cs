using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;

namespace FxDbg.Engine.Scheduling;

public sealed class SingleThreadCommandScheduler : IDisposable
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<ICommandWorkItem> commands = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly ManualResetEventSlim started = new();
    private readonly Action<CancellationToken>? idleWork;
    private readonly Thread thread;
    private readonly object lifecycleGate = new();
    private Exception? terminalFailure;
    private bool disposed;
    private int threadId;

    public SingleThreadCommandScheduler(string threadName, Action<CancellationToken>? idleWork = null)
    {
        if (string.IsNullOrWhiteSpace(threadName))
        {
            throw new ArgumentException("A scheduler thread name is required.", nameof(threadName));
        }

        this.idleWork = idleWork;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName
        };
        thread.Start();
        started.Wait();
    }

    public int ThreadId => Volatile.Read(ref threadId);

    public Task<T> EnqueueAsync<T>(
        Func<CancellationToken, T> command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive command timeout is required.");
        }

        var item = new CommandWorkItem<T>(command, timeout, cancellationToken);
        lock (lifecycleGate)
        {
            if (disposed || shutdown.IsCancellationRequested)
            {
                item.Reject(SchedulerStoppedError());
                return item.Task;
            }

            commands.Add(item);
        }

        return item.Task;
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId == ThreadId)
        {
            throw new InvalidOperationException("The command scheduler cannot dispose itself from its worker thread.");
        }

        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            commands.CompleteAdding();
        }

        shutdown.Cancel();

        if (!thread.Join(ShutdownWait))
        {
            throw new FxDbgException(
                FxDbgErrorCode.OperationTimedOut,
                $"The command scheduler did not stop within {ShutdownWait.TotalSeconds} seconds.");
        }

        started.Dispose();
        shutdown.Dispose();
        commands.Dispose();
    }

    private void Run()
    {
        Volatile.Write(ref threadId, Environment.CurrentManagedThreadId);
        started.Set();
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                if (commands.TryTake(out ICommandWorkItem workItem, idleWork is null ? 100 : 10))
                {
                    workItem.Execute(shutdown.Token);
                    // Status polling must not keep native callback stops pending indefinitely.
                    idleWork?.Invoke(shutdown.Token);
                    continue;
                }

                idleWork?.Invoke(shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (lifecycleGate)
            {
                terminalFailure = exception;
                commands.CompleteAdding();
            }

            shutdown.Cancel();
        }
        finally
        {
            while (commands.TryTake(out ICommandWorkItem pending))
            {
                pending.Reject(SchedulerStoppedError());
            }
        }
    }

    private FxDbgException SchedulerStoppedError()
    {
        return terminalFailure is null
            ? new FxDbgException(FxDbgErrorCode.OperationCancelled, "The command scheduler has stopped.")
            : new FxDbgException(FxDbgErrorCode.EngineExited, "The command scheduler stopped unexpectedly.", terminalFailure);
    }

    private interface ICommandWorkItem
    {
        void Execute(CancellationToken schedulerCancellation);

        void Reject(FxDbgException error);
    }

    private sealed class CommandWorkItem<T> : ICommandWorkItem
    {
        private readonly Func<CancellationToken, T> command;
        private readonly CancellationToken externalCancellation;
        private readonly CancellationTokenSource timeoutCancellation = new();
        private readonly CancellationTokenRegistration externalRegistration;
        private readonly CancellationTokenRegistration timeoutRegistration;
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancellationResourcesDisposed;
        private int state;

        internal CommandWorkItem(
            Func<CancellationToken, T> command,
            TimeSpan timeout,
            CancellationToken externalCancellation)
        {
            this.command = command;
            this.externalCancellation = externalCancellation;
            externalRegistration = externalCancellation.Register(
                () => CancelWhileQueued(FxDbgErrorCode.OperationCancelled, "The command was cancelled."));
            timeoutRegistration = timeoutCancellation.Token.Register(
                () => CancelWhileQueued(FxDbgErrorCode.OperationTimedOut, "The command timed out."));
            timeoutCancellation.CancelAfter(timeout);
        }

        internal Task<T> Task => completion.Task;

        public void Execute(CancellationToken schedulerCancellation)
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                DisposeCancellationResources();
                return;
            }

            using CancellationTokenSource executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                externalCancellation,
                timeoutCancellation.Token,
                schedulerCancellation);
            try
            {
                T result = command(executionCancellation.Token);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (externalCancellation.IsCancellationRequested)
            {
                completion.TrySetException(
                    new FxDbgException(FxDbgErrorCode.OperationCancelled, "The command was cancelled."));
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                completion.TrySetException(
                    new FxDbgException(FxDbgErrorCode.OperationTimedOut, "The command timed out."));
            }
            catch (OperationCanceledException) when (schedulerCancellation.IsCancellationRequested)
            {
                completion.TrySetException(
                    new FxDbgException(FxDbgErrorCode.OperationCancelled, "The command scheduler stopped."));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref state, 2);
                DisposeCancellationResources();
            }
        }

        public void Reject(FxDbgException error)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                completion.TrySetException(error);
            }

            DisposeCancellationResources();
        }

        private void CancelWhileQueued(FxDbgErrorCode code, string message)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                completion.TrySetException(new FxDbgException(code, message));
            }
        }

        private void DisposeCancellationResources()
        {
            if (Interlocked.Exchange(ref cancellationResourcesDisposed, 1) != 0)
            {
                return;
            }

            externalRegistration.Dispose();
            timeoutRegistration.Dispose();
            timeoutCancellation.Dispose();
        }
    }
}
