using System.Text.Json;
using System.Text.Json.Nodes;

namespace FxDbg.McpHost;

/// <summary>Wire schemas preserve Core's nullable fields and string identifiers, including recursive variables.</summary>
internal static class OutputSchemas
{
    private static JsonObject Type(string name) => new() { ["type"] = name };
    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    private static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Type("null")) };
    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject Choice(params string[] values) => new() { ["type"] = "string", ["enum"] = JsonSerializer.SerializeToNode(values) };
    private static JsonObject Object(params (string Name, JsonObject Schema)[] fields)
    {
        var properties = new JsonObject();
        foreach (var field in fields) properties[field.Name] = field.Schema;
        return new() { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
            ["required"] = JsonSerializer.SerializeToNode(fields.Select(x => x.Name)) };
    }
    private static JsonObject Text(bool nullable = false) => nullable ? Nullable(Type("string")) : Type("string");
    private static JsonObject Integer(bool nullable = false) => nullable ? Nullable(Type("integer")) : Type("integer");

    internal static JsonElement For(string tool)
    {
        var definitions = new JsonObject
        {
            ["error"] = Object(("code", Text()), ("message", Text())),
            ["source"] = Object(("filePath", Text()), ("line", Integer()), ("column", Integer(true))),
            ["target"] = Object(("processId", Integer()), ("architecture", Choice("x86", "x64")), ("runtimeVersion", Text()),
                ("runtimeFileVersion", Text(true)), ("launchedByDebugger", Type("boolean")), ("sessionState", Choice("created", "starting", "running", "stopped", "detaching", "terminated", "failed"))),
            ["frame"] = Object(("frameId", Text()), ("threadId", Integer()), ("methodName", Text()), ("moduleName", Text()),
                ("assemblyName", Text()), ("ilOffset", Integer()), ("sourceLocation", Nullable(Ref("source"))), ("appDomain", Text(true))),
            ["thread"] = Object(("threadId", Integer()), ("name", Text(true)), ("appDomain", Text(true)), ("isStopped", Type("boolean"))),
            ["exception"] = Object(("typeName", Text()), ("message", Text(true)), ("threadId", Integer()), ("throwLocation", Nullable(Ref("source"))),
                ("stack", Array(Ref("frame"))), ("isUnhandled", Type("boolean")), ("diagnostic", Text(true))),
            ["stop"] = Object(("reason", Choice("breakpoint", "step", "exception", "userPause", "entry", "processExit")), ("processId", Integer()), ("threadId", Integer()),
                ("appDomain", Text(true)), ("location", Nullable(Ref("source"))), ("breakpointId", Text(true)), ("exception", Nullable(Ref("exception"))),
                ("moduleName", Text(true)), ("methodName", Text(true)), ("briefStack", Array(Ref("frame")))),
            ["breakpoint"] = Object(("breakpointId", Text()), ("requestedLocation", Ref("source")), ("boundLocation", Nullable(Ref("source"))),
                ("state", Choice("pending", "verified", "moved", "unresolved")), ("enabled", Type("boolean")), ("diagnostic", Text(true))),
            ["module"] = Object(("moduleId", Text()), ("name", Text()), ("path", Text()), ("appDomain", Text()),
                ("symbolStatus", Choice("loaded", "missing", "mismatch", "readFailed")), ("pdbPath", Text(true)), ("diagnostic", Text(true))),
            ["variable"] = Object(("name", Text()), ("kind", Choice("argument", "local", "instanceField", "staticField", "arrayElement")),
                ("status", Choice("available", "unavailable", "optimizedAway", "null")), ("typeName", Text(true)), ("displayValue", Text(true)),
                ("referenceId", Text(true)), ("children", Array(Ref("variable"))), ("totalMembers", Integer()), ("diagnostic", Text(true)), ("hasChildren", Type("boolean"))),
            ["operation"] = Object(("operationId", Text()), ("state", Choice("running", "completed", "timedOut", "cancelled", "failed")), ("createdAtUtc", Text())),
            ["status"] = Object(("target", Nullable(Ref("target"))), ("stop", Nullable(Ref("stop"))), ("lastStop", Nullable(Ref("stop"))),
                ("eventSequence", Integer()), ("snapshotAtUtc", Text()), ("activeOperationId", Text(true)))
        };
        void Optional(string definition, string field, JsonObject schema) => definitions[definition]!["properties"]![field] = schema;
        Optional("error", "retryAfterMs", Integer());
        Optional("operation", "finishedAtUtc", Text());
        Optional("operation", "stop", Nullable(Ref("stop")));
        Optional("operation", "error", Ref("error"));
        Optional("status", "operation", Ref("operation"));
        Optional("status", "breakpoints", Array(Ref("breakpoint")));
        Optional("status", "modules", Array(Ref("module")));
        Optional("status", "closing", Type("boolean"));
        Optional("status", "failureCode", Text());
        JsonObject result = tool switch
        {
            "launch" or "attach" or "detach" or "terminate" => Ref("target"),
            "set_breakpoint" => Ref("breakpoint"), "remove_breakpoint" => Object(("removed", Type("boolean"))),
            "continue" or "step" => Ref("operation"), "pause" => Ref("stop"), "status" => Ref("status"),
            "threads" => Array(Ref("thread")), "stack" => Array(Ref("frame")), "variables" => Array(Ref("variable")),
            _ => throw new ArgumentException("Unknown tool output schema.")
        };
        var envelope = Object(("ok", Type("boolean")), ("sessionId", Text(true)));
        envelope["properties"]!["result"] = result;
        envelope["properties"]!["error"] = Ref("error");
        envelope["oneOf"] = JsonNode.Parse("""[{"properties":{"ok":{"const":true}},"required":["result"]},{"properties":{"ok":{"const":false}},"required":["error"]}]""");
        envelope["$defs"] = definitions;
        return JsonSerializer.SerializeToElement(envelope);
    }
}
