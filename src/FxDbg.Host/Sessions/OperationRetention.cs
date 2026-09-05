using System;
using System.Collections.Generic;
using System.Linq;

namespace FxDbg.Host.Sessions;

internal static class OperationRetention
{
    internal static void Trim(Dictionary<string, ExecutionOperation> operations, DateTimeOffset now, TimeSpan retention, int maximum = 128)
    {
        var completed = operations.Values.Where(x => x.IsCompleted).OrderBy(x => x.FinishedAtUtc).ToArray();
        int removeCount = Math.Max(0, completed.Length - maximum);
        foreach (ExecutionOperation operation in completed)
            if (removeCount-- > 0 || now - operation.FinishedAtUtc!.Value >= retention) operations.Remove(operation.Id);
    }
}
