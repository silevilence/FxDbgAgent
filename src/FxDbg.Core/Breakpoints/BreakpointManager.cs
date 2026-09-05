using System;
using System.Collections.Generic;
using System.Linq;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;

namespace FxDbg.Core.Breakpoints;

/// <summary>Owned by one session's command thread. Module identities refer to load instances, not file names.</summary>
public sealed class BreakpointManager : IDisposable
{
    private readonly Dictionary<BreakpointId, Entry> entries = new();
    private readonly Dictionary<string, ISourceBreakpointModule> modules = new(StringComparer.Ordinal);

    public event Action<BreakpointChange>? Changed;

    public IReadOnlyList<BreakpointInfo> List() => entries.Values.Select(entry => entry.Info).ToArray();

    public BreakpointInfo Get(BreakpointId id) => Require(id).Info;

    public BreakpointInfo Set(SourceLocation location, bool enabled = true)
    {
        var entry = new Entry(new BreakpointInfo(BreakpointId.New(), location, null, BreakpointState.Pending, enabled,
            "Waiting for a loaded module with matching source symbols."));
        entries.Add(entry.Info.BreakpointId, entry);
        Changed?.Invoke(new BreakpointChange(entry.Info));
        foreach (ISourceBreakpointModule module in modules.Values)
        {
            Bind(entry, module);
        }

        Publish(entry);
        return entry.Info;
    }

    public BreakpointInfo SetEnabled(BreakpointId id, bool enabled)
    {
        Entry entry = Require(id);
        if (entry.Info.Enabled == enabled) return entry.Info;
        var updated = new List<ISourceBreakpointBinding>();
        try
        {
            foreach (Bound bound in entry.Bindings.Values.SelectMany(value => value))
            {
                bound.Binding.SetEnabled(enabled);
                updated.Add(bound.Binding);
            }
        }
        catch
        {
            foreach (ISourceBreakpointBinding binding in updated) binding.SetEnabled(!enabled);
            throw;
        }

        entry.Info = Copy(entry.Info, enabled: enabled);
        Changed?.Invoke(new BreakpointChange(entry.Info));
        return entry.Info;
    }

    public void Remove(BreakpointId id)
    {
        Entry entry = Require(id);
        foreach (Bound bound in entry.Bindings.Values.SelectMany(value => value)) bound.Binding.Dispose();
        entries.Remove(id);
        Changed?.Invoke(new BreakpointChange(entry.Info, removed: true));
    }

    public void ModuleLoaded(ISourceBreakpointModule module)
    {
        if (modules.ContainsKey(module.Id)) throw new ArgumentException("Module load identity already exists.", nameof(module));
        modules.Add(module.Id, module);
        foreach (Entry entry in entries.Values)
        {
            Bind(entry, module);
            Publish(entry);
        }
    }

    public void SymbolsChanged(string moduleId)
    {
        if (!modules.TryGetValue(moduleId, out ISourceBreakpointModule? module)) return;
        foreach (Entry entry in entries.Values)
        {
            Release(entry, moduleId, true);
            Bind(entry, module);
            Publish(entry);
        }
    }

    public void ModuleUnloaded(string moduleId)
    {
        if (!modules.Remove(moduleId)) return;
        foreach (Entry entry in entries.Values)
        {
            // Unload invalidates native bindings; calling Activate on them can fail with a neutered-object HRESULT.
            Release(entry, moduleId, false);
            Publish(entry);
        }
    }

    public void BindingFailed(string moduleId, BreakpointId id, string diagnostic)
    {
        if (!entries.TryGetValue(id, out Entry? entry) || !modules.ContainsKey(moduleId)) return;
        Release(entry, moduleId, true);
        entry.Resolutions[moduleId] = new SourceBreakpointResolution(true, Array.Empty<BreakpointBindingLocation>(), diagnostic);
        Publish(entry);
    }

    public void Dispose()
    {
        foreach (BreakpointId id in entries.Keys.ToArray()) Remove(id);
        modules.Clear();
    }

    private static void Bind(Entry entry, ISourceBreakpointModule module)
    {
        var bindings = new List<Bound>();
        try
        {
            SourceBreakpointResolution result = module.Resolve(entry.Info.RequestedLocation);
            entry.Resolutions[module.Id] = result;
            foreach (BreakpointBindingLocation location in result.Locations)
            {
                ISourceBreakpointBinding binding = module.Bind(entry.Info.BreakpointId, location);
                try { binding.SetEnabled(entry.Info.Enabled); }
                catch { binding.Dispose(); throw; }
                bindings.Add(new Bound(location.Source, binding));
            }
            entry.Bindings[module.Id] = bindings;
        }
        catch (Exception exception)
        {
            foreach (Bound bound in bindings) bound.Binding.Dispose();
            entry.Resolutions[module.Id] = new SourceBreakpointResolution(true, Array.Empty<BreakpointBindingLocation>(),
                "Source breakpoint could not be bound: " + exception.Message);
        }
    }

    private static void Release(Entry entry, string moduleId, bool deactivate)
    {
        if (entry.Bindings.TryGetValue(moduleId, out List<Bound>? bindings))
        {
            if (deactivate) foreach (Bound bound in bindings) bound.Binding.Dispose();
            entry.Bindings.Remove(moduleId);
        }
        entry.Resolutions.Remove(moduleId);
    }

    private void Publish(Entry entry)
    {
        SourceLocation? location = entry.Bindings.Values.SelectMany(value => value)
            .Select(bound => bound.Source).OrderBy(source => Math.Abs((long)source.Line - entry.Info.RequestedLocation.Line))
            .ThenBy(source => source.Line).FirstOrDefault();
        BreakpointState state = location is null
            ? (entry.Resolutions.Values.Any(result => result.DocumentFound) ? BreakpointState.Unresolved : BreakpointState.Pending)
            : location.Line == entry.Info.RequestedLocation.Line ? BreakpointState.Verified : BreakpointState.Moved;
        string? diagnostic = state switch
        {
            BreakpointState.Moved => $"Requested line {entry.Info.RequestedLocation.Line}; bound to executable line {location!.Line}.",
            BreakpointState.Unresolved => entry.Resolutions.Values.FirstOrDefault(result => result.Diagnostic is not null)?.Diagnostic
                ?? "The source document has no bindable executable locations.",
            BreakpointState.Pending => "Waiting for a loaded module with matching source symbols.",
            _ => null
        };
        if (entry.Info.State == state && entry.Info.BoundLocation?.Line == location?.Line &&
            entry.Info.Diagnostic == diagnostic) return;
        entry.Info = new BreakpointInfo(entry.Info.BreakpointId, entry.Info.RequestedLocation, location, state, entry.Info.Enabled, diagnostic);
        Changed?.Invoke(new BreakpointChange(entry.Info));
    }

    private Entry Require(BreakpointId id) => entries.TryGetValue(id, out Entry? entry) ? entry :
        throw new FxDbgException(FxDbgErrorCode.BreakpointNotFound, "Source breakpoint does not exist: " + id);

    private static BreakpointInfo Copy(BreakpointInfo info, bool enabled) =>
        new(info.BreakpointId, info.RequestedLocation, info.BoundLocation, info.State, enabled, info.Diagnostic);

    private sealed class Entry
    {
        internal Entry(BreakpointInfo info) => Info = info;
        internal BreakpointInfo Info { get; set; }
        internal Dictionary<string, List<Bound>> Bindings { get; } = new();
        internal Dictionary<string, SourceBreakpointResolution> Resolutions { get; } = new();
    }

    private sealed class Bound
    {
        internal Bound(SourceLocation source, ISourceBreakpointBinding binding) { Source = source; Binding = binding; }
        internal SourceLocation Source { get; }
        internal ISourceBreakpointBinding Binding { get; }
    }
}
