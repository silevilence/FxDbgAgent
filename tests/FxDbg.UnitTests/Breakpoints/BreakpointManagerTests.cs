using System;
using System.Collections.Generic;
using System.Linq;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Model;
using Xunit;

namespace FxDbg.UnitTests.Breakpoints;

public sealed class BreakpointManagerTests
{
    [Fact]
    public void Scoped_breakpoint_never_binds_another_or_recreated_domain()
    {
        var manager = new BreakpointManager();
        var selected = new SampleModule("selected-module") { AppDomainId = "session:domain:1" };
        var other = new SampleModule("other-module") { AppDomainId = "session:domain:2" };
        manager.ModuleLoaded(selected);
        manager.ModuleLoaded(other);
        var breakpoint = manager.Set(new SourceLocation("sample.cs", 10), appDomainId: selected.AppDomainId);
        Assert.NotNull(selected.LastBinding);
        Assert.Null(other.LastBinding);
        Assert.Equal(new[] { selected.AppDomainId }, breakpoint.BoundAppDomainIds);
        manager.ModuleUnloaded(selected.Id);
        manager.ModuleLoaded(new SampleModule("replacement-module") { AppDomainId = "session:domain:3" });
        var pending = manager.Get(breakpoint.BreakpointId);
        Assert.Equal(BreakpointState.Pending, pending.State);
        Assert.Empty(pending.BoundAppDomainIds);
        Assert.Equal(selected.AppDomainId, pending.AppDomainId);
    }
    [Fact]
    public void Rebinding_same_line_publishes_changed_source_path()
    {
        var manager = new BreakpointManager();
        var module = new SampleModule("mapped") { FilePath = @"D:\first\sample.cs" };
        manager.ModuleLoaded(module);
        var breakpoint = manager.Set(new SourceLocation("sample.cs", 10));
        module.FilePath = @"D:\second\sample.cs";
        manager.SymbolsChanged(module.Id);
        Assert.Equal(module.FilePath, manager.Get(breakpoint.BreakpointId).BoundLocation!.FilePath);
    }
    [Fact]
    public void Creating_disabled_breakpoint_never_publishes_or_activates_enabled_state()
    {
        var manager = new BreakpointManager();
        var module = new SampleModule("ready");
        manager.ModuleLoaded(module);
        var changes = new List<BreakpointChange>();
        manager.Changed += changes.Add;
        var breakpoint = manager.Set(new SourceLocation("sample.cs", 10), false);
        Assert.False(breakpoint.Enabled);
        Assert.All(changes, change => Assert.False(change.Breakpoint.Enabled));
        Assert.False(module.LastBinding!.EverEnabled);
    }

    [Fact]
    public void Pending_breakpoint_rebinds_after_module_unload_and_reload()
    {
        var manager = new BreakpointManager();
        var changes = new List<BreakpointChange>();
        manager.Changed += changes.Add;
        BreakpointInfo breakpoint = manager.Set(new SourceLocation("sample.cs", 10));
        Assert.Equal(BreakpointState.Pending, breakpoint.State);

        var first = new SampleModule("first");
        manager.ModuleLoaded(first);
        Assert.Equal(BreakpointState.Verified, manager.Get(breakpoint.BreakpointId).State);
        manager.ModuleUnloaded("first");
        Assert.Equal(BreakpointState.Pending, manager.Get(breakpoint.BreakpointId).State);
        manager.ModuleLoaded(new SampleModule("second"));
        Assert.Equal(BreakpointState.Verified, manager.Get(breakpoint.BreakpointId).State);
        Assert.Equal(new[] { BreakpointState.Pending, BreakpointState.Verified,
            BreakpointState.Pending, BreakpointState.Verified }, changes.Select(change => change.Breakpoint.State));
    }

    private sealed class SampleModule : ISourceBreakpointModule
    {
        public SampleModule(string id) => Id = id;
        public string Id { get; }
        public string? AppDomainId { get; set; }
        public bool SymbolsReady { get; set; } = true;
        public bool BindingFails { get; set; }
        public int Line { get; set; } = 10;
        public string FilePath { get; set; } = "sample.cs";
        public Binding? LastBinding { get; private set; }
        public SourceBreakpointResolution Resolve(SourceLocation location) => SymbolsReady
            ? new(true, new[] { new BreakpointBindingLocation(0x06000001, 3, new SourceLocation(FilePath, Line)) }, null)
            : new(false, Array.Empty<BreakpointBindingLocation>(), "PDB not ready");
        public ISourceBreakpointBinding Bind(BreakpointId breakpointId, BreakpointBindingLocation location)
        {
            if (BindingFails) throw new InvalidOperationException("No IL at requested offset");
            return LastBinding = new Binding();
        }
    }

    private sealed class Binding : ISourceBreakpointBinding
    {
        public bool Enabled { get; private set; }
        public bool EverEnabled { get; private set; }
        public void SetEnabled(bool enabled) { Enabled = enabled; EverEnabled |= enabled; }
        public void Dispose() => Enabled = false;
    }

    [Fact]
    public void Symbols_becoming_ready_binds_disabled_breakpoint_and_reports_moved_line()
    {
        var manager = new BreakpointManager();
        var module = new SampleModule("late-pdb") { SymbolsReady = false, Line = 12 };
        manager.ModuleLoaded(module);
        BreakpointInfo breakpoint = manager.Set(new SourceLocation("sample.cs", 11));
        manager.SetEnabled(breakpoint.BreakpointId, false);
        Assert.Equal(BreakpointState.Pending, manager.Get(breakpoint.BreakpointId).State);
        module.SymbolsReady = true;
        manager.SymbolsChanged(module.Id);
        BreakpointInfo bound = manager.Get(breakpoint.BreakpointId);
        Assert.Equal(BreakpointState.Moved, bound.State);
        Assert.Equal(11, bound.RequestedLocation.Line);
        Assert.Equal(12, bound.BoundLocation!.Line);
        Assert.Contains("12", bound.Diagnostic);
        Assert.False(module.LastBinding!.Enabled);
        manager.SetEnabled(breakpoint.BreakpointId, true);
        Assert.True(module.LastBinding.Enabled);
        manager.Remove(breakpoint.BreakpointId);
        Assert.False(module.LastBinding.Enabled);
        Assert.Empty(manager.List());
    }

    [Fact]
    public void Failed_binding_is_unresolved_and_recovers_on_symbol_refresh()
    {
        var manager = new BreakpointManager();
        var module = new SampleModule("failed-binding") { BindingFails = true };
        manager.ModuleLoaded(module);
        BreakpointInfo breakpoint = manager.Set(new SourceLocation("sample.cs", 10));
        Assert.Equal(BreakpointState.Unresolved, breakpoint.State);
        Assert.Contains("No IL", breakpoint.Diagnostic);
        module.BindingFails = false;
        manager.SymbolsChanged(module.Id);
        Assert.Equal(BreakpointState.Verified, manager.Get(breakpoint.BreakpointId).State);
    }

    [Fact]
    public void Unloading_one_of_two_instances_preserves_the_other_binding()
    {
        var manager = new BreakpointManager();
        BreakpointInfo breakpoint = manager.Set(new SourceLocation("sample.cs", 10));
        manager.ModuleLoaded(new SampleModule("domain-one"));
        manager.ModuleLoaded(new SampleModule("domain-two"));
        manager.ModuleUnloaded("domain-one");
        Assert.Equal(BreakpointState.Verified, manager.Get(breakpoint.BreakpointId).State);
        var changes = new List<BreakpointChange>();
        manager.Changed += changes.Add;
        manager.Remove(breakpoint.BreakpointId);
        Assert.True(Assert.Single(changes).Removed);
    }

    [Fact]
    public void Runtime_binding_error_replaces_verified_state_with_unresolved()
    {
        var manager = new BreakpointManager();
        var module = new SampleModule("module");
        manager.ModuleLoaded(module);
        BreakpointInfo breakpoint = manager.Set(new SourceLocation("sample.cs", 10));
        manager.BindingFailed(module.Id, breakpoint.BreakpointId, "JIT rejected breakpoint");
        Assert.Equal(BreakpointState.Unresolved, manager.Get(breakpoint.BreakpointId).State);
        Assert.False(module.LastBinding!.Enabled);
    }
}
