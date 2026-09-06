namespace FxDbg.Core.Model;

public enum SymbolStatus { Loaded, Missing, Mismatch, ReadFailed }

/// <summary>An immutable snapshot of one module load instance.</summary>
public sealed class ModuleInfo
{
    public ModuleInfo(string moduleId, string name, string path, string appDomain, SymbolStatus symbolStatus, string? pdbPath, string? diagnostic, string? appDomainId = null)
    {
        ModuleId = moduleId;
        Name = name;
        Path = path;
        AppDomain = appDomain;
        SymbolStatus = symbolStatus;
        PdbPath = pdbPath;
        Diagnostic = diagnostic;
        AppDomainId = appDomainId;
    }

    public string ModuleId { get; }
    public string Name { get; }
    public string Path { get; }
    public string AppDomain { get; }
    public string? AppDomainId { get; }
    public SymbolStatus SymbolStatus { get; }
    public string? PdbPath { get; }
    public string? Diagnostic { get; }
}
