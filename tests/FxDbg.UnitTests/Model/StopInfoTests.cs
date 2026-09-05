using System.Collections.Generic;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class StopInfoTests
{
    [Theory]
    [InlineData(StopReason.Entry)]
    [InlineData(StopReason.UserPause)]
    public void Process_wide_stops_can_report_that_no_managed_thread_is_available(StopReason reason)
    {
        var stop = new StopInfo(reason, 42, 0, null, null);
        Assert.Equal(0, stop.ThreadId);
    }

    [Fact]
    public void Stop_keeps_method_module_and_brief_stack_context()
    {
        var frame = new StackFrameInfo(new FrameId("7:0"), 7, "Sample.Run", "Sample.dll", "Sample", 12, null, "DefaultDomain");

        var stop = new StopInfo(
            StopReason.Breakpoint,
            42,
            7,
            "DefaultDomain",
            null,
            moduleName: "Sample.dll",
            methodName: "Sample.Run",
            briefStack: new List<StackFrameInfo> { frame });

        Assert.Equal("Sample.dll", stop.ModuleName);
        Assert.Equal("Sample.Run", stop.MethodName);
        Assert.Same(frame, Assert.Single(stop.BriefStack));
    }
}
