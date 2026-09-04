using System;
using System.Collections.Generic;
using System.Globalization;
using FxDbg.Core.Errors;
using FxDbg.Core.Requests;
using FxDbg.Core.Sessions;

namespace FxDbg.Engine;

internal enum EngineMode
{
    Launch,
    Attach
}

internal sealed class EngineOptions
{
    private EngineOptions(
        EngineMode mode,
        SessionId sessionId,
        string? executablePath,
        int? processId,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TargetArchitecture architecture,
        bool stopAtEntry,
        bool holdSession,
        int verificationCycles,
        TimeSpan timeout)
    {
        Mode = mode;
        SessionId = sessionId;
        ExecutablePath = executablePath;
        ProcessId = processId;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        Architecture = architecture;
        StopAtEntry = stopAtEntry;
        HoldSession = holdSession;
        VerificationCycles = verificationCycles;
        Timeout = timeout;
    }

    internal EngineMode Mode { get; }
    internal SessionId SessionId { get; }
    internal string? ExecutablePath { get; }
    internal int? ProcessId { get; }
    internal IReadOnlyList<string> Arguments { get; }
    internal string? WorkingDirectory { get; }
    internal IReadOnlyDictionary<string, string> Environment { get; }
    internal TargetArchitecture Architecture { get; }
    internal bool StopAtEntry { get; }
    internal bool HoldSession { get; }
    internal int VerificationCycles { get; }
    internal TimeSpan Timeout { get; }

    internal LaunchRequest ToLaunchRequest() => new(
        SessionId,
        ExecutablePath!,
        Arguments,
        WorkingDirectory,
        Environment,
        Architecture,
        StopAtEntry,
        Timeout);

    internal static EngineOptions Parse(string[] args)
    {
        if (args.Length == 0 || (args[0] != "launch" && args[0] != "attach"))
        {
            throw Invalid("Expected engine mode: launch or attach.");
        }

        EngineMode mode = args[0] == "launch" ? EngineMode.Launch : EngineMode.Attach;
        string? executable = null;
        int? processId = null;
        string? workingDirectory = null;
        TargetArchitecture architecture = IntPtr.Size == 4 ? TargetArchitecture.X86 : TargetArchitecture.X64;
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        SessionId sessionId = SessionId.New();
        bool stopAtEntry = false;
        bool holdSession = false;
        int verificationCycles = 0;
        var arguments = new List<string>();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (option == "--stop-at-entry")
            {
                stopAtEntry = true;
            }
            else if (option == "--hold-session")
            {
                holdSession = true;
            }
            else if (option == "--arg")
            {
                arguments.Add(Next(args, ref index, option));
            }
            else if (option == "--env")
            {
                string assignment = Next(args, ref index, option);
                int separator = assignment.IndexOf('=');
                if (separator <= 0)
                {
                    throw Invalid("--env expects NAME=VALUE.");
                }

                environment[assignment.Substring(0, separator)] = assignment.Substring(separator + 1);
            }
            else
            {
                string value = Next(args, ref index, option);
                switch (option)
                {
                    case "--exe": executable = value; break;
                    case "--pid": processId = ParsePositiveInt(value, option); break;
                    case "--working-directory": workingDirectory = value; break;
                    case "--arch": architecture = ParseArchitecture(value); break;
                    case "--timeout-ms": timeout = TimeSpan.FromMilliseconds(ParsePositiveInt(value, option)); break;
                    case "--session-id": sessionId = ParseSessionId(value); break;
                    case "--verification-cycles": verificationCycles = ParsePositiveInt(value, option); break;
                    default: throw Invalid("Unknown engine option: " + option);
                }
            }
        }

        if (mode == EngineMode.Launch && string.IsNullOrWhiteSpace(executable))
        {
            throw Invalid("Launch requires --exe.");
        }

        if (mode == EngineMode.Attach && processId is null)
        {
            throw Invalid("Attach requires --pid.");
        }

        if (mode == EngineMode.Attach && stopAtEntry)
        {
            throw Invalid("--stop-at-entry is valid only for launch.");
        }

        return new EngineOptions(
            mode,
            sessionId,
            executable,
            processId,
            arguments,
            workingDirectory,
            environment,
            architecture,
            stopAtEntry,
            holdSession,
            verificationCycles,
            timeout);
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result <= 0)
        {
            throw Invalid(option + " expects a positive integer.");
        }

        return result;
    }

    private static SessionId ParseSessionId(string value)
    {
        if (!Guid.TryParse(value, out Guid result) || result == Guid.Empty)
        {
            throw Invalid("--session-id expects a non-empty UUID.");
        }

        return new SessionId(result);
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw Invalid(option + " requires a value.");
        }

        return args[index];
    }

    private static TargetArchitecture ParseArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "x86" => TargetArchitecture.X86,
        "x64" => TargetArchitecture.X64,
        _ => throw Invalid("Engine --arch must be x86 or x64.")
    };

    private static FxDbgException Invalid(string message) => new(FxDbgErrorCode.InvalidRequest, message);
}
