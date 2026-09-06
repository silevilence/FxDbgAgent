using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FxDbg.Core.Errors;

namespace FxDbg.Core.Model;

public sealed class SourcePathMapping
{
    public SourcePathMapping(string buildRoot, string localRoot, string? module = null)
    {
        BuildRoot = Normalize(buildRoot);
        LocalRoot = Normalize(localRoot);
        if (module is not null && (string.IsNullOrWhiteSpace(module) || module.IndexOfAny(new[] { '*', '?' }) >= 0))
            throw Invalid("Module selector must be an exact module file name or absolute path.");
        Module = module is not null && (module.Contains("\\") || module.Contains("/")) ? Normalize(module) : module;
    }
    public string BuildRoot { get; }
    public string LocalRoot { get; }
    public string? Module { get; }

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Invalid("Source mapping requires an absolute Windows path.");
        string windows = path.Replace('/', '\\');
        if (!(windows.Length >= 3 && char.IsLetter(windows[0]) && windows[1] == ':' && windows[2] == '\\') &&
            !windows.StartsWith("\\\\", StringComparison.Ordinal)) throw Invalid("Source mapping requires an absolute Windows path.");
        try { return Path.GetFullPath(windows).TrimEnd('\\'); }
        catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
        { throw Invalid("Source mapping path is invalid."); }
    }
    internal static FxDbgException Invalid(string message) => new(FxDbgErrorCode.InvalidRequest, message);
}

/// <summary>Immutable session configuration. Match Windows paths by complete segments, never by substrings.</summary>
public sealed class SourcePathMapper
{
    public SourcePathMapper(IReadOnlyList<SourcePathMapping>? mappings = null)
    {
        if (mappings?.Count > 128) throw SourcePathMapping.Invalid("At most 128 source mappings are allowed.");
        var copy = mappings is null ? Array.Empty<SourcePathMapping>() : mappings.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            if (copy[i] is null) throw SourcePathMapping.Invalid("Source mappings cannot contain null entries.");
            for (int j = 0; j < i; j++)
                if (Same(copy[i].Module, copy[j].Module) &&
                    ((Same(copy[i].BuildRoot, copy[j].BuildRoot) && !Same(copy[i].LocalRoot, copy[j].LocalRoot)) ||
                     (Same(copy[i].LocalRoot, copy[j].LocalRoot) && !Same(copy[i].BuildRoot, copy[j].BuildRoot))))
                    throw SourcePathMapping.Invalid("Source mappings are ambiguous in the same module scope.");
        }
        Mappings = new ReadOnlyCollection<SourcePathMapping>(copy);
    }
    public IReadOnlyList<SourcePathMapping> Mappings { get; }

    public SourceLocation ToLocal(SourceLocation location, string modulePath)
    {
        string mapped = Map(location.FilePath, modulePath, false);
        return Same(mapped, location.FilePath) ? location : new SourceLocation(mapped, location.Line, location.Column, location.OriginalFilePath ?? location.FilePath);
    }
    public SourceLocation ToBuild(SourceLocation location, string modulePath)
        => new(Map(location.FilePath, modulePath, true), location.Line, location.Column);

    private string Map(string path, string modulePath, bool reverse)
    {
        if (Mappings.Count == 0) return path;
        string input = SourcePathMapping.Normalize(path);
        var candidates = Mappings.Where(mapping => mapping.Module is null || Same(mapping.Module, modulePath) || Same(mapping.Module, Path.GetFileName(modulePath)))
            .Where(mapping => Prefix(input, reverse ? mapping.LocalRoot : mapping.BuildRoot)).ToArray();
        if (candidates.Any(mapping => mapping.Module is not null)) candidates = candidates.Where(mapping => mapping.Module is not null).ToArray();
        if (candidates.Length == 0) return path;
        int longest = candidates.Max(mapping => (reverse ? mapping.LocalRoot : mapping.BuildRoot).Length);
        string[] results = candidates.Where(mapping => (reverse ? mapping.LocalRoot : mapping.BuildRoot).Length == longest)
            .Select(mapping => (reverse ? mapping.BuildRoot : mapping.LocalRoot) + input.Substring(longest)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (results.Length != 1) throw SourcePathMapping.Invalid("Source path matches conflicting module mappings.");
        return results[0];
    }
    private static bool Prefix(string path, string root) => Same(path, root) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
