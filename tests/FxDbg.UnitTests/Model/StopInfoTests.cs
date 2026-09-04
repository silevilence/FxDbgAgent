using System.Collections.Generic;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class StopInfoTests
{
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
