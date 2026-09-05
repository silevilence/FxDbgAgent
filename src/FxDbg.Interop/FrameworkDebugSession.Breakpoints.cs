using System;
using System.Collections.Generic;
using System.Linq;
using ClrDebug;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Model;
using FxDbg.Core.Events;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    private readonly BreakpointManager breakpoints = new();
    private readonly Dictionary<object, DebugModule> modules = new();
    private DateTime nextSymbolPoll;

    public event Action<BreakpointChange> BreakpointChanged
    {
        add => breakpoints.Changed += value;
        remove => breakpoints.Changed -= value;
    }

    public BreakpointId? HitBreakpointId { get; private set; }
    public int? StoppedThreadId { get; private set; }
    public bool IsStopped => pendingEntryController is not null;
    public bool HasExited => processExited;

    public IReadOnlyList<ModuleInfo> GetModules()
    {
        ThrowIfDisposed();
        ThrowIfWrongThread();
        return modules.Values.Select(module => module.Snapshot).ToArray();
    }

    public IReadOnlyList<BreakpointInfo> GetBreakpoints()
    {
        ThrowIfDisposed();
        ThrowIfWrongThread();
        return breakpoints.List();
    }

    public BreakpointInfo SetBreakpoint(SourceLocation source) => WithSynchronizedTarget(() => breakpoints.Set(source));

    public BreakpointInfo SetBreakpointEnabled(BreakpointId id, bool enabled) =>
        WithSynchronizedTarget(() => breakpoints.SetEnabled(id, enabled));

    public void RemoveBreakpoint(BreakpointId id) => WithSynchronizedTarget(() => { breakpoints.Remove(id); return true; });

    public void RefreshSymbols() => WithSynchronizedTarget(() =>
    {
        foreach (DebugModule module in modules.Values)
        {
            ReloadModuleSymbols(module);
        }
        return true;
    });

    public void Continue()
    {
        RequireStopped();
        callbackPairing.Continue(() => (pendingEntryController ?? process).Continue(false));
        pendingEntryController = null;
        framesById.Clear();
        variableReader = null;
        HitBreakpointId = null;
        StoppedThreadId = null;
        CurrentStop = null;
        domain.Resume();
    }

    private T WithSynchronizedTarget<T>(Func<T> action)
    {
        RequireActive();
        if (IsStopped) return action();
        process.Stop(0);
        callbackPairing.RecordManualStop();
        try { return action(); }
        finally { callbackPairing.Continue(() => process.Continue(false)); }
    }

    private bool HandleBreakpointCallback(CallbackEnvelope envelope)
    {
        switch (envelope.EventArgs)
        {
            case LoadModuleCorDebugManagedCallbackEventArgs loaded:
                var module = new DebugModule(loaded.Module);
                modules.Add(loaded.Module.Raw, module);
                breakpoints.ModuleLoaded(module);
                domain.RecordModuleChange(ModuleChangeKind.Loaded, module.Snapshot);
                break;
            case UnloadModuleCorDebugManagedCallbackEventArgs unloaded:
                if (modules.TryGetValue(unloaded.Module.Raw, out DebugModule? removed))
                {
                    breakpoints.ModuleUnloaded(removed.Id);
                    modules.Remove(unloaded.Module.Raw);
                    domain.RecordModuleChange(ModuleChangeKind.Unloaded, removed.Snapshot);
                    removed.Dispose();
                }
                break;
            case UpdateModuleSymbolsCorDebugManagedCallbackEventArgs updated:
                if (modules.TryGetValue(updated.Module.Raw, out DebugModule? changed))
                {
                    ReloadModuleSymbols(changed);
                }
                break;
            case BreakpointCorDebugManagedCallbackEventArgs hit:
                HitBreakpointId = modules.Values.Select(moduleItem => moduleItem.FindBreakpoint(hit.Breakpoint)).FirstOrDefault(id => id is not null);
                StoppedThreadId = hit.Thread.Id;
                pendingEntryController = envelope.Controller;
                StopAt(hit.Thread, FxDbg.Core.Sessions.StopReason.Breakpoint);
                return true;
            case StepCompleteCorDebugManagedCallbackEventArgs step:
                stepper = null;
                pendingEntryController = envelope.Controller;
                StopAt(step.Thread, FxDbg.Core.Sessions.StopReason.Step);
                return true;
            case BreakpointSetErrorCorDebugManagedCallbackEventArgs failed:
                foreach (DebugModule loadedModule in modules.Values)
                {
                    BreakpointId? id = loadedModule.FindBreakpoint(failed.Breakpoint);
                    if (id is not null) breakpoints.BindingFailed(loadedModule.Id, id,
                        "CLR rejected the source breakpoint: " + failed.Error);
                }
                break;
        }
        return false;
    }

    private void ClearModules()
    {
        framesById.Clear();
        variableReader = null;
        foreach (DebugModule module in modules.Values)
        {
            breakpoints.ModuleUnloaded(module.Id);
            domain.RecordModuleChange(ModuleChangeKind.Unloaded, module.Snapshot);
            module.Dispose();
        }
        modules.Clear();
    }

    private void PollSymbolFiles()
    {
        if (processExited || DateTime.UtcNow < nextSymbolPoll) return;
        nextSymbolPoll = DateTime.UtcNow.AddMilliseconds(500);
        DebugModule[] changed = modules.Values.Where(module => module.SymbolsFileChanged()).ToArray();
        if (changed.Length == 0) return;
        WithSynchronizedTarget(() =>
        {
            foreach (DebugModule module in changed)
            {
                ReloadModuleSymbols(module);
            }
            return true;
        });
    }

    private void ReloadModuleSymbols(DebugModule module)
    {
        ModuleInfo previous = module.Snapshot;
        module.ReloadSymbols();
        breakpoints.SymbolsChanged(module.Id);
        if (previous.SymbolStatus != module.Snapshot.SymbolStatus || previous.Diagnostic != module.Snapshot.Diagnostic)
            domain.RecordModuleChange(ModuleChangeKind.SymbolsChanged, module.Snapshot);
    }
}
