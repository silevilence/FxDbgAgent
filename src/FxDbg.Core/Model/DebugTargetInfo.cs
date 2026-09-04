using System;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Model;

public sealed class DebugTargetInfo
{
    public DebugTargetInfo(
        int processId,
        TargetArchitecture architecture,
        string runtimeVersion,
        bool launchedByDebugger,
        DebugSessionState sessionState)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        ProcessId = processId;
        Architecture = architecture;
        RuntimeVersion = string.IsNullOrWhiteSpace(runtimeVersion)
            ? throw new ArgumentException("A runtime version is required.", nameof(runtimeVersion))
            : runtimeVersion;
        LaunchedByDebugger = launchedByDebugger;
        SessionState = sessionState;
    }

    public int ProcessId { get; }

    public TargetArchitecture Architecture { get; }

    public string RuntimeVersion { get; }

    public bool LaunchedByDebugger { get; }

    public DebugSessionState SessionState { get; }
}
