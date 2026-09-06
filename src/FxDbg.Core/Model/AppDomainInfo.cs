namespace FxDbg.Core.Model;

/// <summary>Identity of one AppDomain lifetime within a session; names and runtime IDs can be reused.</summary>
public sealed class AppDomainInfo
{
    public AppDomainInfo(string appDomainId, string name, int runtimeId)
    { AppDomainId = appDomainId; Name = name; RuntimeId = runtimeId; }
    public string AppDomainId { get; }
    public string Name { get; }
    public int RuntimeId { get; }
}
