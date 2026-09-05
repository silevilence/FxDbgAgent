using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FxDbg.Host.Sessions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FxDbg.UnitTests.Host;

public sealed class ExecutionOperationTests
{
    [Fact]
    public async Task First_terminal_observation_wins_and_polling_does_not_mutate_it()
    {
        var operation = new ExecutionOperation();
        operation.Finish("completed", new JObject { ["reason"] = "breakpoint" });
        operation.Finish("timedOut", code: "operation_timed_out");
        JObject first = operation.Snapshot();
        first["state"] = "tampered";
        Assert.Equal("completed", (string?)operation.Snapshot()["state"]);
        Assert.Equal("breakpoint", (string?)(await operation.Completion.Task)["stop"]?["reason"]);
    }

    [Fact]
    public void Retention_evicts_oldest_terminal_records_then_expires_them_without_removing_running_work()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var records = new Dictionary<string, ExecutionOperation>();
        for (int index = 0; index < 130; index++)
        {
            var operation = new ExecutionOperation(() => now);
            operation.Finish("completed"); records.Add(operation.Id, operation); now += TimeSpan.FromSeconds(1);
        }
        var running = new ExecutionOperation(() => now); records.Add(running.Id, running);
        OperationRetention.Trim(records, now, TimeSpan.FromMinutes(10));
        Assert.Equal(129, records.Count);
        Assert.Equal(128, records.Values.Count(x => x.IsCompleted));
        OperationRetention.Trim(records, now + TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        Assert.Same(running, Assert.Single(records).Value);
    }
}
