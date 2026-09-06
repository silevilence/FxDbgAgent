using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrDebug;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Model;
using FxDbg.Symbols.Windows;

namespace FxDbg.Interop;

internal sealed class DebugModule : ISourceBreakpointModule, IDisposable
{
    private readonly CorDebugModule module;
    private readonly List<NativeBinding> bindings = new();
    private WindowsModuleSymbols? symbols;
    private readonly string path;
    private DateTime symbolWriteTime;
    private long symbolLength;
    private readonly Dictionary<int, string> methodNames = new();
    private readonly string appDomain;
    private ModuleInfo? snapshot;
    internal SourcePathMapper SourceMapper { get; set; } = new();

    internal DebugModule(CorDebugModule module)
    {
        this.module = module;
        path = module.Name;
        appDomain = module.Assembly.AppDomain.Name;
        Id = Guid.NewGuid().ToString("D");
        ReloadSymbols();
    }

    public string Id { get; }
    internal ModuleInfo Snapshot => snapshot!;
    internal bool HasSymbols => symbols?.Status == SymbolStatus.Loaded;

    internal SourceLocation? Resolve(int token, int offset)
    {
        SourceLocation? source = symbols?.Resolve(token, offset);
        return source is null ? null : SourceMapper.ToLocal(source, path);
    }
    internal IReadOnlyList<LocalVariableSlot> GetLocals(int token, int offset) => symbols?.GetLocals(token, offset) ?? Array.Empty<LocalVariableSlot>();
    internal string GetMethodName(int token)
    {
        if (methodNames.TryGetValue(token, out string? name)) return name;
        name = MetadataNames.Method(module.GetFunctionFromToken(new mdMethodDef(token)));
        if (methodNames.Count < 100000) methodNames.Add(token, name);
        return name;
    }
    internal int GetStepRangeEnd(int token, int offset, int codeSize) => symbols?.GetStepRangeEnd(token, offset, codeSize) ?? codeSize;

    public SourceBreakpointResolution Resolve(SourceLocation location)
    {
        if (symbols is null) return new SourceBreakpointResolution(false, Array.Empty<BreakpointBindingLocation>(), "This module has no local PE/PDB files.");
        SourceBreakpointResolution result = symbols.Resolve(SourceMapper.ToBuild(location, path));
        return new SourceBreakpointResolution(result.DocumentFound, result.Locations.Select(point =>
            new BreakpointBindingLocation(point.MethodToken, point.IlOffset, SourceMapper.ToLocal(point.Source, path))).ToArray(), result.Diagnostic);
    }

    public ISourceBreakpointBinding Bind(BreakpointId breakpointId, BreakpointBindingLocation location)
    {
        CorDebugFunction function = module.GetFunctionFromToken(new mdMethodDef(location.MethodToken));
        var binding = new NativeBinding(breakpointId, function.ILCode.CreateBreakpoint(location.IlOffset), bindings);
        bindings.Add(binding);
        return binding;
    }

    internal BreakpointId? FindBreakpoint(CorDebugBreakpoint breakpoint) =>
        bindings.FirstOrDefault(binding => Equals(binding.Breakpoint.Raw, breakpoint.Raw))?.Id;

    internal void ReloadSymbols()
    {
        symbols?.Dispose();
        symbols = null;
        try
        {
            if (File.Exists(path)) symbols = WindowsModuleSymbols.Open(path);
            var file = new FileInfo(Path.ChangeExtension(path, ".pdb"));
            symbolWriteTime = file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue;
            symbolLength = file.Exists ? file.Length : 0;
            snapshot = new ModuleInfo(Id, Path.GetFileName(path), path, appDomain, symbols?.Status ?? SymbolStatus.Missing,
                symbols?.PdbPath, symbols?.Diagnostic ?? (symbols is null ? "This module has no local PE/PDB files." : null));
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
        {
            symbolWriteTime = DateTime.MinValue;
            symbolLength = -1;
            snapshot = new ModuleInfo(Id, path, path, appDomain, SymbolStatus.ReadFailed, null, "Cannot inspect symbol files: " + error.Message);
        }
    }

    internal bool SymbolsFileChanged()
    {
        try
        {
            if (!File.Exists(path)) return false;
            var file = new FileInfo(Path.ChangeExtension(path, ".pdb"));
            return (file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue) != symbolWriteTime ||
                (file.Exists ? file.Length : 0) != symbolLength;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
        {
            return snapshot?.SymbolStatus != SymbolStatus.ReadFailed;
        }
    }

    public void Dispose()
    {
        bindings.Clear();
        methodNames.Clear();
        symbols?.Dispose();
        symbols = null;
    }

    private sealed class NativeBinding : ISourceBreakpointBinding
    {
        private readonly List<NativeBinding> owner;
        private bool disposed;
        internal NativeBinding(BreakpointId id, CorDebugFunctionBreakpoint breakpoint, List<NativeBinding> owner)
        { Id = id; Breakpoint = breakpoint; this.owner = owner; }
        internal BreakpointId Id { get; }
        internal CorDebugFunctionBreakpoint Breakpoint { get; }
        public void SetEnabled(bool enabled) => Breakpoint.Activate(enabled);
        public void Dispose()
        {
            if (disposed) return;
            Breakpoint.Activate(false);
            disposed = true;
            owner.Remove(this);
        }
    }
}
