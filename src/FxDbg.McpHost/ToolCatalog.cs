using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FxDbg.McpHost;

/// <summary>The public contract, used both for discovery and boundary validation.</summary>
public static class ToolCatalog
{
    private static JsonObject Text() => new() { ["type"] = "string", ["minLength"] = 1 };
    private static JsonObject Number(int min, int max, int? value = null) => new()
        { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["default"] = value };
    private static JsonObject Flag(bool value) => new() { ["type"] = "boolean", ["default"] = value };
    private static JsonObject Choice(params string[] values) => new()
        { ["type"] = "string", ["enum"] = new JsonArray(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
    private static JsonObject Fields(params (string Name, JsonObject Schema)[] fields)
    {
        var result = new JsonObject();
        foreach (var field in fields) result[field.Name] = field.Schema;
        return result;
    }

    public static IReadOnlyList<Tool> Tools { get; } =
    [
        Define("launch", "Launch a local .NET Framework target. Copy the returned sessionId. Use stopAtEntry=true before setting initial breakpoints.",
            Fields(("exe", Text()), ("args", new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }),
                ("cwd", Text()), ("env", new() { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }),
                ("arch", Choice("auto", "x86", "x64")), ("stopAtEntry", Flag(false))), ["exe"], false),
        Define("attach", "Attach to one local Desktop CLR process without terminating it on detach.",
            Fields(("pid", Number(1, int.MaxValue)), ("arch", Choice("auto", "x86", "x64"))), ["pid"], false),
        Define("set_breakpoint", "Create a source breakpoint with file and line, or update enabled using only breakpointId and enabled. Report moved/pending states.",
            Fields(("file", Text()), ("line", Number(1, int.MaxValue)), ("enabled", Flag(true)), ("breakpointId", Text())), []),
        Define("remove_breakpoint", "Remove a source breakpoint by its returned ID.", Fields(("breakpointId", Text())), ["breakpointId"]),
        Define("continue", "Resume exactly once; waitForStop=false returns an operationId to poll with debug_status. Old frame/reference IDs expire.",
            Fields(("waitForStop", Flag(true))), []),
        Define("step", "Step one specified thread into/over/out. Requires a stopped session. Old frame/reference IDs expire.",
            Fields(("threadId", Number(1, int.MaxValue)), ("kind", Choice("into", "over", "out")), ("waitForStop", Flag(true))), ["threadId", "kind"]),
        Define("pause", "Pause a running target and complete its active operation with userPause.", Fields(), []),
        Define("status", "Read session state, last stop, symbols/breakpoints and optionally an operation result; safe to poll while running.",
            Fields(("operationId", Text())), [], true, true),
        Define("threads", "Read managed threads from a stopped session; use returned IDs, never guess them.", Fields(), [], true, true),
        Define("stack", "Read managed frames for one stopped thread. Use returned opaque frameId for variables.",
            Fields(("threadId", Number(1, int.MaxValue)), ("start", Number(0, 100000, 0)), ("count", Number(1, 1024, 32))), ["threadId"], true, true),
        Define("variables", "Read fields/arguments/locals without getters, ToString or evaluation. References belong to this session and stop only.",
            Fields(("frameId", Text()), ("referenceId", Text()), ("start", Number(0, int.MaxValue, 0)), ("count", Number(1, 1024, 100)),
                ("maxDepth", Number(0, 8, 1)), ("maxStringLength", Number(1, 32768, 256))), ["frameId"], true, true),
        Define("evaluate", "Interpret a bounded read-only expression in a stopped frame. Never executes target code. Only the documented intrinsic whitelist is accepted.",
            Fields(("frameId", Text()), ("expression", new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 }),
                ("evaluationTimeoutMs", Number(1, 1000, 250)), ("count", Number(1, 1024, 100)), ("maxDepth", Number(0, 8, 1)),
                ("maxStringLength", Number(1, 32768, 256))), ["frameId", "expression"], true, true),
        Define("detach", "Safely detach and leave the target running. Ends the debugging session.", Fields(), []),
        Define("terminate", "Terminate only a target launched by this debugger. Attached targets are rejected.", Fields(), [])
    ];

    private static Tool Define(string name, string description, JsonObject properties, string[] required, bool session = true, bool readOnly = false)
    {
        if (name is "status" or "threads" or "stack" or "variables" or "evaluate" or "set_breakpoint") properties["appDomainId"] = Text();
        if (name is "launch" or "attach") properties["sourceMappings"] = new JsonObject
        {
            ["type"] = "array", ["maxItems"] = 128,
            ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = Fields(("buildRoot", Text()), ("localRoot", Text()), ("module", Text())),
                ["required"] = new JsonArray("buildRoot", "localRoot") }
        };
        properties["timeoutMs"] = Number(1, 240000, 10000);
        if (session) properties["sessionId"] = Text();
        if (properties["arch"] is JsonObject arch) arch["default"] = "auto";
        var schema = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
            ["required"] = new JsonArray((session ? required.Prepend("sessionId") : required).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        };
        if (name == "set_breakpoint")
            schema["oneOf"] = JsonNode.Parse("""[{"required":["file","line"],"not":{"required":["breakpointId"]}},{"required":["breakpointId","enabled"],"not":{"anyOf":[{"required":["file"]},{"required":["line"]},{"required":["appDomainId"]}]}}]""");
        return new Tool
        {
            Name = "debug_" + name, Description = description, InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = OutputSchemas.For(name),
            Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = !readOnly, OpenWorldHint = false, IdempotentHint = readOnly }
        };
    }

    public static Tool Find(string name) => Tools.SingleOrDefault(x => x.Name == name)
        ?? throw new McpProtocolException("Unknown debug tool.", McpErrorCode.InvalidParams);

    public static void Validate(Tool tool, JsonElement arguments)
    {
        ValidateValue(tool.InputSchema, arguments);
        if (tool.Name == "debug_set_breakpoint")
        {
            bool update = arguments.TryGetProperty("breakpointId", out _);
            bool file = arguments.TryGetProperty("file", out _), line = arguments.TryGetProperty("line", out _);
            if (update ? file || line || arguments.TryGetProperty("appDomainId", out _) || !arguments.TryGetProperty("enabled", out _) : !file || !line)
                throw new ArgumentException("Use file + line to create, or breakpointId + enabled to update a breakpoint.");
        }
    }

    private static void ValidateValue(JsonElement schema, JsonElement value)
    {
        string? type = schema.GetProperty("type").GetString();
        bool valid = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!valid) throw new ArgumentException("Tool argument has an invalid JSON type.");
        if (type == "string" && schema.TryGetProperty("minLength", out _) && string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException("Tool string argument cannot be empty.");
        if (type == "string" && schema.TryGetProperty("maxLength", out var maxLength) && value.GetString()!.Length > maxLength.GetInt32())
            throw new ArgumentException("Tool string argument exceeds its length limit.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => x.GetString() == value.GetString()))
            throw new ArgumentException("Tool argument is outside the advertised enum.");
        if (type == "integer" && (value.GetInt32() < schema.GetProperty("minimum").GetInt32() || value.GetInt32() > schema.GetProperty("maximum").GetInt32()))
            throw new ArgumentException("Tool integer argument is outside the advertised limits.");
        if (type == "array")
        {
            if (schema.TryGetProperty("maxItems", out var maximum) && value.GetArrayLength() > maximum.GetInt32()) throw new ArgumentException("Tool array exceeds its item limit.");
            foreach (var item in value.EnumerateArray()) ValidateValue(schema.GetProperty("items"), item);
        }
        if (type != "object") return;
        bool hasProperties = schema.TryGetProperty("properties", out var properties);
        if (schema.TryGetProperty("required", out var required))
            foreach (var field in required.EnumerateArray())
                if (!value.TryGetProperty(field.GetString()!, out _)) throw new ArgumentException("A required tool argument is missing.");
        foreach (var item in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(item.Name, out var fieldSchema)) ValidateValue(fieldSchema, item.Value);
            else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.Object) ValidateValue(extra, item.Value);
            else throw new ArgumentException("Unknown tool argument.");
        }
    }
}
