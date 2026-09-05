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

    internal DebugModule(CorDebugModule module)
    {
        this.module = module;
        path = module.Name;
        Id = Guid.NewGuid().ToString("D");
        ReloadSymbols();
    }

    public string Id { get; }

    internal SourceLocation? Resolve(int token, int offset) => symbols?.Resolve(token, offset);
    internal string? GetMethodName(int token) => symbols?.GetMethodName(token);
    internal int GetStepRangeEnd(int token, int offset, int codeSize) => symbols?.GetStepRangeEnd(token, offset, codeSize) ?? codeSize;

    public SourceBreakpointResolution Resolve(SourceLocation location) => symbols?.Resolve(location)
        ?? new SourceBreakpointResolution(false, Array.Empty<BreakpointBindingLocation>(), "This module has no local PE/PDB files.");

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
        if (File.Exists(path)) symbols = WindowsModuleSymbols.Open(path);
        var file = new FileInfo(Path.ChangeExtension(path, ".pdb"));
        symbolWriteTime = file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue;
        symbolLength = file.Exists ? file.Length : 0;
    }

    internal bool SymbolsFileChanged()
    {
        if (!File.Exists(path)) return false;
        var file = new FileInfo(Path.ChangeExtension(path, ".pdb"));
        return (file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue) != symbolWriteTime ||
            (file.Exists ? file.Length : 0) != symbolLength;
    }

    public void Dispose()
    {
        bindings.Clear();
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
