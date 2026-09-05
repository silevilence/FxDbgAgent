namespace FxDbg.Core.Requests;

public static class TargetArchitectureWireName
{
    public static string Format(TargetArchitecture architecture) => architecture.ToString().ToLowerInvariant();
}
