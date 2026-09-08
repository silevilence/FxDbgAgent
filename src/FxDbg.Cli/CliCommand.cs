using System.Runtime.InteropServices;
using FxDbg.Core.Errors;
using FxDbg.Core.Breakpoints;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;
using FxDbg.Engine.Protocol;
using Newtonsoft.Json.Linq;

namespace FxDbg.Cli;

public sealed class CliCommand
{
    private CliCommand(string method, SessionId sessionId, JObject parameters, string? engineDirectory, TimeSpan timeout)
    { Method = method; SessionId = sessionId; Parameters = parameters; EngineDirectory = engineDirectory; Timeout = timeout; }

    public string Method { get; }
    public SessionId SessionId { get; }
    public JObject Parameters { get; }
    public string? EngineDirectory { get; }
    public TimeSpan Timeout { get; }
    public bool StartsSession => Method is "launch" or "attach";

    public static CliCommand Parse(string[] args)
    {
        if (args.Length == 0) throw Invalid("A command is required. Use --help.");
        string method = args[0] switch
        {
            "launch" or "attach" or "continue" or "pause" or "step" or "wait" or "threads" or "stack" or "variables" or "evaluate" or "detach" or "terminate" or "state" or "modules" or "events" => args[0],
            "break" => "break.set", "breakpoints" => "break.list", "remove-break" => "break.remove", "enable-break" => "break.enable",
            "exceptions" => "exceptions.configure", "refresh-symbols" => "symbols.refresh",
            _ => throw Invalid("Unknown command. Use --help.")
        };
        var values = new JObject();
        var arguments = new JArray();
        var environment = new JObject();
        SessionId? id = null;
        string? engineDirectory = null;
        int timeout = 10000;
        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            if (!Allowed(method, option)) throw Invalid("Option " + option + " does not apply to this command.");
            if (option is "--stop-at-entry" or "--stopAtEntry") { values["stopAtEntry"] = true; continue; }
            if (index + 1 == args.Length) throw Invalid("Missing value for " + option);
            string value = args[++index];
            switch (option)
            {
                case "--session":
                    if (!Guid.TryParse(value, out Guid guid) || guid == Guid.Empty) throw Invalid("Session must be a GUID.");
                    id = new SessionId(guid); break;
                case "--engine-dir": engineDirectory = Path.GetFullPath(value); break;
                case "--timeout-ms": timeout = Positive(value, option); break;
                case "--exe": values["executablePath"] = RequiredPath(value); break;
                case "--cwd": values["workingDirectory"] = RequiredPath(value); break;
                case "--arch": if (value is not ("auto" or "x86" or "x64")) throw Invalid("Architecture must be auto, x86 or x64."); values["architecture"] = value; break;
                case "--arg": arguments.Add(value); break;
                case "--args": foreach (string argument in SplitArguments(value)) arguments.Add(argument); break;
                case "--env":
                    int separator = value.IndexOf('=');
                    if (separator <= 0) throw Invalid("Environment assignment must be NAME=VALUE.");
                    environment[value[..separator]] = value[(separator + 1)..]; break;
                case "--pid": values["processId"] = Positive(value, option); break;
                case "--app-domain": values["appDomainId"] = value; break;
                case "--source-maps":
                    var mappings = JArray.Parse(File.ReadAllText(RequiredPath(value)));
                    _ = new SourcePathMapper(mappings.ToObject<SourcePathMapping[]>());
                    values["sourceMappings"] = mappings; break;
                case "--thread": values["threadId"] = Positive(value, option); break;
                case "--line": values["line"] = Positive(value, option); break;
                case "--file": values["file"] = RequiredPath(value); break;
                case "--frame": values["frameId"] = value; break;
                case "--expression": values["expression"] = value; break;
                case "--condition": values["condition"] = value; break;
                case "--hit-condition": values["hitCondition"] = value; break;
                case "--exception-rules": values["rules"] = WireJson.Value(ExceptionStopConfiguration.ParseRules(value)); break;
                case "--evaluation-timeout-ms": values["evaluationTimeoutMs"] = Positive(value, option); break;
                case "--reference": values["referenceId"] = value; break;
                case "--breakpoint": values["breakpointId"] = value; break;
                case "--kind": if (value is not ("into" or "over" or "out")) throw Invalid("Step kind must be into, over or out."); values["kind"] = value; break;
                case "--start": case "--count": case "--max-depth": case "--max-string-length":
                    if (!int.TryParse(value, out int number) || number < 0) throw Invalid("Nonnegative integer required for " + option);
                    values[option switch { "--max-depth" => "maxDepth", "--max-string-length" => "maxStringLength", _ => option[2..] }] = number; break;
                case "--enabled": case "--first-chance":
                    if (!bool.TryParse(value, out bool flag)) throw Invalid("Boolean required for " + option);
                    values[option == "--enabled" ? "enabled" : "firstChance"] = flag; break;
                default: throw Invalid("Unknown option: " + option);
            }
        }
        bool starts = method is "launch" or "attach";
        if (!starts && id is null) throw Invalid("--session is required.");
        if (method == "launch" && values["executablePath"] is null) throw Invalid("Launch requires --exe.");
        if (method == "attach" && values["processId"] is null) throw Invalid("Attach requires --pid.");
        if (method == "break.set" && (values["file"] is null || values["line"] is null)) throw Invalid("Break requires --file and --line.");
        if (method == "break.set") _ = new BreakpointCondition((string?)values["condition"], (string?)values["hitCondition"]);
        if (method is "step" or "stack" && values["threadId"] is null) throw Invalid("--thread is required.");
        if (method == "step" && values["kind"] is null) throw Invalid("--kind is required.");
        if (method == "variables" && string.IsNullOrWhiteSpace((string?)values["frameId"])) throw Invalid("Variables requires --frame from stack output.");
        if (method == "evaluate" && (string.IsNullOrWhiteSpace((string?)values["frameId"]) || string.IsNullOrWhiteSpace((string?)values["expression"]))) throw Invalid("Evaluate requires --frame and --expression.");
        if (method is "break.remove" or "break.enable" && string.IsNullOrWhiteSpace((string?)values["breakpointId"])) throw Invalid("--breakpoint is required.");
        if (timeout > 240000) throw Invalid("Timeout cannot exceed four minutes.");
        if (method == "launch") { values["arguments"] = arguments; values["environment"] = environment; }
        return new CliCommand(method, id ?? SessionId.New(), values, engineDirectory, TimeSpan.FromMilliseconds(timeout));
    }

    private static int Positive(string value, string option) => int.TryParse(value, out int number) && number > 0 ? number : throw Invalid("Positive integer required for " + option);
    private static bool Allowed(string method, string option)
    {
        if (option is "--session" or "--timeout-ms") return true;
        if (option == "--app-domain" && method is "threads" or "stack" or "variables" or "evaluate" or "break.set") return true;
        if (method is "launch" or "attach" && option is "--engine-dir" or "--arch" or "--source-maps") return true;
        return method switch
        {
            "launch" => option is "--exe" or "--cwd" or "--arg" or "--args" or "--env" or "--stop-at-entry" or "--stopAtEntry",
            "attach" => option == "--pid",
            "break.set" => option is "--file" or "--line" or "--condition" or "--hit-condition",
            "break.remove" => option == "--breakpoint",
            "break.enable" => option is "--breakpoint" or "--enabled",
            "step" => option is "--thread" or "--kind",
            "stack" => option is "--thread" or "--start" or "--count",
            "variables" => option is "--frame" or "--reference" or "--start" or "--count" or "--max-depth" or "--max-string-length",
            "evaluate" => option is "--frame" or "--expression" or "--evaluation-timeout-ms" or "--count" or "--max-depth" or "--max-string-length",
            "exceptions.configure" => option is "--first-chance" or "--exception-rules",
            _ => false
        };
    }
    private static string RequiredPath(string value) => string.IsNullOrWhiteSpace(value) ? throw Invalid("A nonempty path is required.") : Path.GetFullPath(value);
    private static FxDbgException Invalid(string message) => new(FxDbgErrorCode.InvalidRequest, message);

    private static IEnumerable<string> SplitArguments(string text)
    {
        IntPtr argv = CommandLineToArgvW("fxdbg " + text, out int count);
        if (argv == IntPtr.Zero) throw Invalid("Unable to parse --args.");
        try { for (int index = 1; index < count; index++) yield return Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!; }
        finally { LocalFree(argv); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
