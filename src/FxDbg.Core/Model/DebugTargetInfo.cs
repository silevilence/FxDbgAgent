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
        DebugSessionState sessionState,
        string? runtimeFileVersion = null)
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
        RuntimeFileVersion = runtimeFileVersion;
    }

    public int ProcessId { get; }

    public TargetArchitecture Architecture { get; }

    public string RuntimeVersion { get; }

    /// <summary>File version of the clr.dll loaded by the target, distinct from the CLR hosting moniker.</summary>
    public string? RuntimeFileVersion { get; }

    public bool LaunchedByDebugger { get; }

    public DebugSessionState SessionState { get; }
}
