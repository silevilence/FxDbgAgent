using System;
using System.IO;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;

namespace FxDbg.Host.Engine;

public sealed class EngineProcessPaths
{
    public EngineProcessPaths(string x86Path, string x64Path)
    {
        X86Path = RequiredFullPath(x86Path, nameof(x86Path));
        X64Path = RequiredFullPath(x64Path, nameof(x64Path));
    }

    public string X86Path { get; }

    public string X64Path { get; }

    public string For(TargetArchitecture architecture)
    {
        string path = architecture switch
        {
            TargetArchitecture.X86 => X86Path,
            TargetArchitecture.X64 => X64Path,
            _ => throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "An Engine path requires a resolved architecture.")
        };

        if (!File.Exists(path))
        {
            throw new FxDbgException(FxDbgErrorCode.TargetNotFound, $"Engine executable was not found: {path}");
        }

        return path;
    }

    private static string RequiredFullPath(string path, string parameterName) =>
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("An Engine executable path is required.", parameterName)
            : Path.GetFullPath(path);
}
