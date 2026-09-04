using System;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;
using Xunit;

namespace FxDbg.UnitTests.Requests;

public sealed class EngineRequestTests
{
    [Fact]
    public void Request_rejects_a_nonpositive_timeout_with_the_unified_error_code()
    {
        FxDbgException error = Assert.Throws<FxDbgException>(
            () => new ContinueRequest(SessionId.New(), TimeSpan.Zero));

        Assert.Equal(FxDbgErrorCode.InvalidRequest, error.Code);
    }
}
