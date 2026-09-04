using System;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Requests;

public sealed class StatusRequest : EngineRequest
{
    public StatusRequest(SessionId sessionId, TimeSpan timeout) : base(sessionId, timeout)
    {
    }
}

public sealed class ThreadsRequest : EngineRequest
{
    public ThreadsRequest(SessionId sessionId, TimeSpan timeout) : base(sessionId, timeout)
    {
    }
}

public sealed class StackRequest : EngineRequest
{
    public StackRequest(SessionId sessionId, int threadId, int startFrame, int maxFrames, TimeSpan timeout)
        : base(sessionId, timeout)
    {
        if (threadId <= 0 || startFrame < 0 || maxFrames <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Thread and stack paging values are invalid.");
        }

        ThreadId = threadId;
        StartFrame = startFrame;
        MaxFrames = maxFrames;
    }

    public int ThreadId { get; }

    public int StartFrame { get; }

    public int MaxFrames { get; }
}

public sealed class VariablesRequest : EngineRequest
{
    public VariablesRequest(
        SessionId sessionId,
        FrameId frameId,
        VariableReferenceId? referenceId,
        int start,
        int count,
        int maxDepth,
        int maxStringLength,
        TimeSpan timeout)
        : base(sessionId, timeout)
    {
        if (frameId is null || start < 0 || count <= 0 || maxDepth < 0 || maxStringLength <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Frame and variable paging limits are invalid.");
        }

        FrameId = frameId;
        ReferenceId = referenceId;
        Start = start;
        Count = count;
        MaxDepth = maxDepth;
        MaxStringLength = maxStringLength;
    }

    public FrameId FrameId { get; }

    public VariableReferenceId? ReferenceId { get; }

    public int Start { get; }

    public int Count { get; }

    public int MaxDepth { get; }

    public int MaxStringLength { get; }
}
