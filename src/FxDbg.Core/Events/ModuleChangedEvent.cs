using System;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Events;

public enum ModuleChangeKind { Loaded, SymbolsChanged, Unloaded }

public sealed class ModuleChangedEvent : EngineEvent
{
    public ModuleChangedEvent(SessionId sessionId, long sequence, DateTimeOffset occurredAtUtc, ModuleChangeKind change, ModuleInfo module)
        : base(sessionId, sequence, occurredAtUtc)
    {
        Change = change;
        Module = module;
    }

    public ModuleChangeKind Change { get; }
    public ModuleInfo Module { get; }
}
