using FxDbg.Core.Errors;
using Xunit;

namespace FxDbg.UnitTests.Errors;

public sealed class FxDbgErrorCodeWireNameTests
{
    [Theory]
    [InlineData(FxDbgErrorCode.InvalidRequest, "invalid_request")]
    [InlineData(FxDbgErrorCode.CoreClrNotSupported, "core_clr_not_supported")]
    [InlineData(FxDbgErrorCode.OperationTimedOut, "operation_timed_out")]
    public void Format_and_parse_round_trip(FxDbgErrorCode code, string wireName)
    {
        Assert.Equal(wireName, FxDbgErrorCodeWireName.Format(code));
        Assert.True(FxDbgErrorCodeWireName.TryParse(wireName, out FxDbgErrorCode parsed));
        Assert.Equal(code, parsed);
    }

    [Fact]
    public void Unknown_wire_name_is_rejected()
    {
        Assert.False(FxDbgErrorCodeWireName.TryParse("not_a_real_code", out FxDbgErrorCode code));
        Assert.Equal(FxDbgErrorCode.InternalError, code);
    }
}
