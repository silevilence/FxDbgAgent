using System;

namespace FxDbg.Core.Model;

public sealed class ManagedThreadInfo
{
    public ManagedThreadInfo(int threadId, string? name, string? appDomain, bool isStopped)
    {
        if (threadId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threadId));
        }

        ThreadId = threadId;
        Name = name;
        AppDomain = appDomain;
        IsStopped = isStopped;
    }

    public int ThreadId { get; }

    public string? Name { get; }

    public string? AppDomain { get; }

    public bool IsStopped { get; }
}
