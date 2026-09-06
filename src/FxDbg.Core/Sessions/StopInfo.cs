using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FxDbg.Core.Model;

namespace FxDbg.Core.Sessions;

public sealed class StopInfo
{
    public StopInfo(
        StopReason reason,
        int processId,
        int threadId,
        string? appDomain,
        SourceLocation? location,
        BreakpointId? breakpointId = null,
        ExceptionInfo? exception = null,
        string? moduleName = null,
        string? methodName = null,
        IReadOnlyList<StackFrameInfo>? briefStack = null, string? appDomainId = null)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        if (threadId < 0 || (threadId == 0 && reason != StopReason.ProcessExit && reason != StopReason.Entry && reason != StopReason.UserPause))
        {
            throw new ArgumentOutOfRangeException(nameof(threadId));
        }

        Reason = reason;
        ProcessId = processId;
        ThreadId = threadId;
        AppDomain = appDomain;
        AppDomainId = appDomainId;
        Location = location;
        BreakpointId = breakpointId;
        Exception = exception;
        ModuleName = moduleName;
        MethodName = methodName;
        BriefStack = new ReadOnlyCollection<StackFrameInfo>(
            briefStack is null ? Array.Empty<StackFrameInfo>() : new List<StackFrameInfo>(briefStack));
    }

    public StopReason Reason { get; }

    public int ProcessId { get; }

    public int ThreadId { get; }

    public string? AppDomain { get; }
    public string? AppDomainId { get; }

    public SourceLocation? Location { get; }

    public BreakpointId? BreakpointId { get; }

    public ExceptionInfo? Exception { get; }

    public string? ModuleName { get; }

    public string? MethodName { get; }

    public IReadOnlyList<StackFrameInfo> BriefStack { get; }
}
