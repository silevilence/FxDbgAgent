using System;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Core.Errors;
using FxDbg.Platform;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class DebugPrivilegeTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1300, false)]
    public void Missing_privilege_is_distinct_from_success(bool success, int error, bool expected) =>
        Assert.Equal(expected, WindowsDebugPrivilege.WasAssigned(success, error));

    [Theory]
    [InlineData(false, 5)]
    [InlineData(false, 1300)]
    [InlineData(true, 5)]
    public void Adjustment_errors_are_actionable_access_denied(bool success, int error)
    {
        var exception = Assert.Throws<FxDbgException>(() => WindowsDebugPrivilege.WasAssigned(success, error));
        Assert.Equal(FxDbgErrorCode.AccessDenied, exception.Code);
        Assert.Contains("current debugger token", exception.Message);
    }

    [Fact]
    public void Restores_token_when_operation_fails()
    {
        var adjustment = new Adjustment();
        Assert.Throws<InvalidOperationException>(() => WindowsDebugPrivilege.Execute<int>(
            () => throw new InvalidOperationException("target exited"), () => adjustment));
        Assert.True(adjustment.Disposed);
    }

    [Fact]
    public void Failed_restore_disposes_a_successfully_created_session()
    {
        var session = new Adjustment();
        Assert.Throws<InvalidOperationException>(() => WindowsDebugPrivilege.Execute(() => session,
            () => new Adjustment { FailRestore = true }));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Concurrent_scope_and_process_start_wait_until_restoration()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var adjustment = new Adjustment();
        Task<int> first = Task.Run(() => WindowsDebugPrivilege.Execute(() =>
        {
            entered.SetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return 1;
        }, () => adjustment));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> second = Task.Run(() => WindowsDebugPrivilege.WithoutAdjustment(() => adjustment.Disposed));
        try { Assert.False(second.IsCompleted); }
        finally { release.Set(); }
        Assert.Equal(1, await first);
        Assert.True(await second);
    }

    [Fact]
    public void Native_current_token_allows_same_process_architecture_detection()
    {
        int result = WindowsDebugPrivilege.Execute(() => new FxDbg.Host.Architecture.ProcessArchitectureDetector()
            .Detect(Environment.ProcessId) == FxDbg.Core.Requests.TargetArchitecture.X64 ? 64 : 32);
        Assert.Equal(IntPtr.Size * 8, result);
    }

    private sealed class Adjustment : IDisposable
    {
        internal bool Disposed;
        internal bool FailRestore;
        public void Dispose() { Disposed = true; if (FailRestore) throw new InvalidOperationException("restore failed"); }
    }
}
