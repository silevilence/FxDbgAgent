using System;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Requests;

public sealed class ContinueRequest : EngineRequest
{
    public ContinueRequest(SessionId sessionId, TimeSpan timeout) : base(sessionId, timeout)
    {
    }
}

public sealed class PauseRequest : EngineRequest
{
    public PauseRequest(SessionId sessionId, TimeSpan timeout) : base(sessionId, timeout)
    {
    }
}

public sealed class StepRequest : EngineRequest
{
    public StepRequest(SessionId sessionId, StepKind kind, int threadId, TimeSpan timeout)
        : base(sessionId, timeout)
    {
        if (threadId <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive thread ID is required for stepping.");
        }

        Kind = kind;
        ThreadId = threadId;
    }

    public StepKind Kind { get; }

    public int ThreadId { get; }
}
