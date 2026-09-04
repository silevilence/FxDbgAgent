using System;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Requests;

public abstract class EngineRequest
{
    protected EngineRequest(SessionId sessionId, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive request timeout is required.");
        }

        RequestId = Guid.NewGuid();
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        Timeout = timeout;
    }

    public Guid RequestId { get; }

    public SessionId SessionId { get; }

    public TimeSpan Timeout { get; }
}
