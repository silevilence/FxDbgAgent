using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FxDbg.Core.Errors;
using FxDbg.Core.Sessions;

namespace FxDbg.Core.Requests;

public sealed class LaunchRequest : EngineRequest
{
    public LaunchRequest(
        SessionId sessionId,
        string executablePath,
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TargetArchitecture architecture,
        bool stopAtEntry,
        TimeSpan timeout)
        : base(sessionId, timeout)
    {
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "An executable path is required.")
            : executablePath;
        Arguments = new ReadOnlyCollection<string>(
            arguments is null ? Array.Empty<string>() : new List<string>(arguments));
        WorkingDirectory = workingDirectory;
        Environment = new ReadOnlyDictionary<string, string>(
            environment is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : CopyEnvironment(environment));
        Architecture = architecture;
        StopAtEntry = stopAtEntry;
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public TargetArchitecture Architecture { get; }

    public bool StopAtEntry { get; }

    private static Dictionary<string, string> CopyEnvironment(IReadOnlyDictionary<string, string> source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in source)
        {
            copy.Add(pair.Key, pair.Value);
        }

        return copy;
    }
}

public sealed class AttachRequest : EngineRequest
{
    public AttachRequest(SessionId sessionId, int processId, TargetArchitecture architecture, TimeSpan timeout)
        : base(sessionId, timeout)
    {
        if (processId <= 0)
        {
            throw new FxDbgException(FxDbgErrorCode.InvalidRequest, "A positive process ID is required.");
        }

        ProcessId = processId;
        Architecture = architecture;
    }

    public int ProcessId { get; }

    public TargetArchitecture Architecture { get; }
}

public sealed class DetachRequest : EngineRequest
{
    public DetachRequest(SessionId sessionId, TimeSpan timeout) : base(sessionId, timeout)
    {
    }
}

public sealed class TerminateRequest : EngineRequest
{
    public TerminateRequest(SessionId sessionId, int exitCode, TimeSpan timeout) : base(sessionId, timeout)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
