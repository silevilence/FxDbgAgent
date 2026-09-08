using System;
using System.Collections.Generic;
using System.Linq;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Events;
using FxDbg.Core.Model;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    private readonly Dictionary<object, AppDomainInfo> appDomains = new();
    private readonly Dictionary<object, ManagedThreadInfo> observedThreads = new();
    private long appDomainGeneration;

    public IReadOnlyList<AppDomainInfo> GetAppDomains()
    {
        ThrowIfDisposed();
        ThrowIfWrongThread();
        return appDomains.Values.ToArray();
    }

    private AppDomainInfo GetAppDomain(CorDebugAppDomain native, bool refreshName = false)
    {
        if (appDomains.TryGetValue(native.Raw, out AppDomainInfo? found))
        {
            if (!refreshName) return found;
            string name = native.Name;
            if (name == found.Name) return found;
            var updated = new AppDomainInfo(found.AppDomainId, name, found.RuntimeId);
            appDomains[native.Raw] = updated;
            foreach (DebugModule module in modules.Values.Where(module => module.AppDomainId == found.AppDomainId)) module.UpdateAppDomainName(name);
            domain.RecordAppDomainChange(AppDomainChangeKind.Updated, updated);
            return updated;
        }
        if (appDomains.Count >= 1024) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "AppDomain count exceeds 1024.");
        var info = new AppDomainInfo(SessionId + ":domain:" + ++appDomainGeneration, native.Name, native.Id);
        appDomains.Add(native.Raw, info);
        domain.RecordAppDomainChange(AppDomainChangeKind.Created, info);
        return info;
    }

    private void RequireAppDomain(string? appDomainId)
    {
        if (appDomainId is not null && !appDomains.Values.Any(item => item.AppDomainId == appDomainId))
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "AppDomain ID is unknown, unloaded, or belongs to another session; query status again.");
    }

    private void HandleAppDomainCallback(CallbackEnvelope envelope)
    {
        switch (envelope.EventArgs)
        {
            case NameChangeCorDebugManagedCallbackEventArgs changed:
                if (changed.AppDomain is not null) GetAppDomain(changed.AppDomain, refreshName: true);
                if (changed.Thread is not null) RecordThread(changed.Thread, changed.AppDomain, ThreadChangeKind.Updated);
                break;
            case CreateAppDomainCorDebugManagedCallbackEventArgs created:
                GetAppDomain(created.AppDomain);
                break;
            case ExitAppDomainCorDebugManagedCallbackEventArgs exited:
                unloadQuietDeadline = DateTime.UtcNow.AddMilliseconds(100);
                if (!appDomains.TryGetValue(exited.AppDomain.Raw, out AppDomainInfo? removed)) break;
                unloadingBreakpointDomains.Remove(removed.AppDomainId);
                foreach (var pair in modules.Where(pair => pair.Value.AppDomainId == removed.AppDomainId).ToArray())
                {
                    breakpoints.ModuleUnloaded(pair.Value.Id);
                    modules.Remove(pair.Key);
                    domain.RecordModuleChange(ModuleChangeKind.Unloaded, pair.Value.Snapshot);
                    pair.Value.Dispose();
                }
                framesById.Clear();
                ClearVariableReferences();
                appDomains.Remove(exited.AppDomain.Raw);
                domain.RecordAppDomainChange(AppDomainChangeKind.Exited, removed);
                break;
            case CreateThreadCorDebugManagedCallbackEventArgs createdThread:
                RecordThread(createdThread.Thread, createdThread.AppDomain, ThreadChangeKind.Created);
                break;
            case ExitThreadCorDebugManagedCallbackEventArgs exitedThread:
                // Desktop CLR can no longer answer Thread.AppDomain after a thread exits.
                // The callback's lifetime metadata is the final observed snapshot; never dereference the dead thread.
                if (observedThreads.TryGetValue(exitedThread.Thread.Raw, out ManagedThreadInfo? last))
                {
                    observedThreads.Remove(exitedThread.Thread.Raw);
                    domain.RecordThreadChange(ThreadChangeKind.Exited, new ManagedThreadInfo(last.ThreadId, last.Name, last.AppDomain, false, last.AppDomainId));
                }
                break;
        }
    }

    private void RecordThread(CorDebugThread thread, CorDebugAppDomain? nativeDomain, ThreadChangeKind change)
    {
        if (observedThreads.Count >= 10000 && !observedThreads.ContainsKey(thread.Raw)) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Thread tracking exceeds 10000 entries.");
        AppDomainInfo? info = nativeDomain is null ? null : GetAppDomain(nativeDomain);
        var snapshot = new ManagedThreadInfo(thread.Id, null, info?.Name, IsStopped, info?.AppDomainId);
        observedThreads[thread.Raw] = snapshot;
        domain.RecordThreadChange(change, snapshot);
    }
}
