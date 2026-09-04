using System.Collections.Generic;
using FxDbg.Core.Model;
using Xunit;

namespace FxDbg.UnitTests.Model;

public sealed class ExceptionInfoTests
{
    [Fact]
    public void Exception_stack_is_a_snapshot_of_observed_frames()
    {
        var source = new List<StackFrameInfo>
        {
            new(new FrameId("7:0"), 7, "Sample.Run", "Sample.dll", "Sample", 12, null, "DefaultDomain")
        };
        var exception = new ExceptionInfo("System.Exception", "boom", 7, null, source);

        source.Clear();

        Assert.Single(exception.Stack);
    }
}
