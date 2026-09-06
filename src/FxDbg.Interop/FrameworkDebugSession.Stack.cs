using System;
using System.Collections.Generic;
using System.Threading;
using ClrDebug;
using FxDbg.Core.Errors;
using FxDbg.Core.Model;

namespace FxDbg.Interop;

public sealed partial class FrameworkDebugSession
{
    private long stopGeneration;
    private readonly Dictionary<FrameId, FrameHandle> framesById = new();

    public IReadOnlyList<ManagedThreadInfo> GetThreads(CancellationToken cancellationToken = default, string? appDomainId = null)
    {
        RequireStopped();
        RequireAppDomain(appDomainId);
        var threads = new List<ManagedThreadInfo>();
        int visited = 0;
        foreach (CorDebugThread thread in process.EnumerateThreads())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 10000) throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Thread enumeration exceeds 10000 entries.");
            AppDomainInfo info = GetAppDomain(thread.AppDomain);
            if (appDomainId is not null && appDomainId != info.AppDomainId) continue;
            var snapshot = new ManagedThreadInfo(thread.Id, MetadataNames.ThreadName(thread), info.Name, true, info.AppDomainId);
            observedThreads[thread.Raw] = snapshot;
            threads.Add(snapshot);
        }
        return threads;
    }

    public IReadOnlyList<StackFrameInfo> GetStack(int threadId, int startFrame = 0, int maxFrames = 32,
        CancellationToken cancellationToken = default, string? appDomainId = null)
    {
        RequireStopped();
        RequireAppDomain(appDomainId);
        if (threadId <= 0 || startFrame < 0 || startFrame > 100000 || maxFrames <= 0 || maxFrames > 1024)
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "Stack requires a positive thread, nonnegative start (at most 100000), and 1-1024 frames.");
        return CaptureStack(RequireThread(threadId), startFrame, maxFrames, cancellationToken, appDomainId);
    }

    private IReadOnlyList<StackFrameInfo> CaptureStack(CorDebugThread thread, int startFrame, int maxFrames,
        CancellationToken cancellationToken = default, string? appDomainId = null)
    {
        var result = new List<StackFrameInfo>();
        int index = 0;
        int matched = 0;
        foreach (CorDebugChain chain in thread.EnumerateChains())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!chain.IsManaged) continue;
            foreach (CorDebugFrame frame in chain.EnumerateFrames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Raw is not ICorDebugILFrame raw) continue;
                int depth = index++;
                var ilFrame = new CorDebugILFrame(raw);
                CorDebugFunction function = frame.Function;
                AppDomainInfo info = GetAppDomain(function.Module.Assembly.AppDomain);
                if (appDomainId is not null && appDomainId != info.AppDomainId) continue;
                if (matched++ < startFrame) continue;
                GetIPResult ip = ilFrame.IP;
                var id = new FrameId(stopGeneration + ":" + thread.Id + ":" + depth);
                framesById[id] = new FrameHandle(thread.Id, ilFrame, info);
                modules.TryGetValue(function.Module.Raw, out DebugModule? module);
                result.Add(new StackFrameInfo(id, thread.Id,
                    module?.GetMethodName(unchecked((int)function.Token.Value)) ?? MetadataNames.Method(function),
                    function.Module.Name, function.Module.Assembly.Name, unchecked((uint)ip.pnOffset),
                    module?.Resolve(unchecked((int)function.Token.Value), ip.pnOffset), info.Name, info.AppDomainId));
                if (result.Count == maxFrames) return result;
            }
        }
        return result;
    }

    private sealed class FrameHandle
    {
        internal FrameHandle(int threadId, CorDebugILFrame frame, AppDomainInfo appDomain) { ThreadId = threadId; Frame = frame; AppDomain = appDomain; }
        internal int ThreadId { get; }
        internal CorDebugILFrame Frame { get; }
        internal AppDomainInfo AppDomain { get; }
    }
}
