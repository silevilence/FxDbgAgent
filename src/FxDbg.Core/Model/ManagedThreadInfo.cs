using System;

namespace FxDbg.Core.Model;

public sealed class ManagedThreadInfo
{
    public ManagedThreadInfo(int threadId, string? name, string? appDomain, bool isStopped, string? appDomainId = null)
    {
        if (threadId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threadId));
        }

        ThreadId = threadId;
        Name = name;
        AppDomain = appDomain;
        IsStopped = isStopped;
        AppDomainId = appDomainId;
    }

    public int ThreadId { get; }

    public string? Name { get; }

    public string? AppDomain { get; }
    public string? AppDomainId { get; }

    public bool IsStopped { get; }
}
