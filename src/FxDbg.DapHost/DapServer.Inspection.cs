using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FxDbg.DapHost;

internal sealed partial class DapServer
{
    private sealed record VariableHandle(string Frame, string? Reference, bool Indexed, int? Total = null);
    private readonly Dictionary<int, string> frames = [];
    private readonly Dictionary<int, VariableHandle> variables = [];
    private readonly Dictionary<string, List<string>> sourceBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> breakpointNumbers = new(StringComparer.Ordinal);
    private JArray knownThreads = new();
    private readonly Dictionary<int, int> stackTotals = [];
    private int nextHandle, nextBreakpoint;

    private void ClearHandles() { frames.Clear(); variables.Clear(); stackTotals.Clear(); }
    private int Handle()
    {
        if (frames.Count + variables.Count >= 10000 || nextHandle == int.MaxValue) throw Invalid("DAP handle limit reached; resume before reading more values.");
        return ++nextHandle;
    }

    private async Task<JObject> SetBreakpoints(JObject args, CancellationToken token)
    {
        var source = args["source"] as JObject ?? throw Invalid("source is required.");
        if ((int?)source["sourceReference"] is > 0) throw Invalid("Only source files on disk are supported.");
        string suppliedPath = Text(source, "path");
        if (!Path.IsPathFullyQualified(suppliedPath)) throw Invalid("Source path must be absolute.");
        string file = Path.GetFullPath(suppliedPath);
        var requested = args["breakpoints"] as JArray ?? new JArray();
        if (requested.Count > 1024) throw Invalid("At most 1024 breakpoints per source file.");
        var lines = new List<int>();
        foreach (var item in requested)
        {
            if (item is not JObject breakpoint) throw Invalid("breakpoints must contain objects.");
            foreach (string field in new[] { "condition", "hitCondition", "logMessage" })
                if (breakpoint[field]?.Type is not null and not JTokenType.Null) throw Invalid(field + " is not supported.");
            int line = checked(Integer(breakpoint, "line") + (linesStartAt1 ? 0 : 1));
            if (line <= 0) throw Invalid("Invalid source line.");
            lines.Add(line);
        }
        if (!sourceBreakpoints.TryGetValue(file, out var existing)) sourceBreakpoints[file] = existing = [];
        foreach (string id in existing.ToArray())
        {
            await Invoke("remove_breakpoint", new() { ["breakpointId"] = id }, token);
            existing.Remove(id); breakpointNumbers.Remove(id); breakpointStates.Remove(id);
        }
        var result = new JArray();
        foreach (int line in lines)
        {
            var bound = await Invoke("set_breakpoint", new() { ["file"] = file, ["line"] = line }, token);
            string id = (string)bound["breakpointId"]!;
            existing.Add(id);
            if (!breakpointNumbers.TryGetValue(id, out int number)) breakpointNumbers[id] = number = checked(++nextBreakpoint);
            breakpointStates[id] = bound.ToString(Formatting.None);
            result.Add(ToBreakpoint(bound, number));
        }
        if (existing.Count == 0) sourceBreakpoints.Remove(file);
        return new() { ["breakpoints"] = result };
    }

    private JObject ToBreakpoint(JToken value, int number)
    {
        var location = value["boundLocation"]?.Type == JTokenType.Object ? value["boundLocation"]! : value["requestedLocation"]!;
        string state = (string)value["state"]!;
        return new() { ["id"] = number, ["verified"] = state is "verified" or "moved",
            ["message"] = state == "moved" ? "Moved to the nearest executable line." : (string?)value["diagnostic"] ?? state,
            ["source"] = Source(location), ["line"] = (int)location["line"]! - (linesStartAt1 ? 0 : 1) };
    }

    private static JObject Source(JToken location) => new() { ["name"] = Path.GetFileName((string)location["filePath"]!), ["path"] = location["filePath"]!.DeepClone() };

    private async Task<JObject> Threads(CancellationToken token)
    {
        if (!targetStopped) return new() { ["threads"] = knownThreads.DeepClone() };
        var result = new JArray();
        foreach (var thread in await Invoke("threads", new(), token))
            result.Add(new JObject { ["id"] = thread["threadId"]!.DeepClone(), ["name"] = ((string?)thread["name"] ?? "Managed thread " + thread["threadId"]) + " (" + thread["appDomain"] + ")" });
        knownThreads = result;
        return new() { ["threads"] = result.DeepClone() };
    }

    private async Task<JObject> Stack(JObject args, CancellationToken token)
    {
        int start = Integer(args, "startFrame", 0), count = Integer(args, "levels", 0);
        if (start < 0 || count < 0) throw Invalid("Invalid stack page.");
        if (count > 128) throw Invalid("Stack pages are limited to 128 frames; use startFrame/levels paging.");
        bool unpaged = count == 0;
        if (unpaged) count = 128;
        int thread = Integer(args, "threadId");
        var page = (JArray)await Invoke("stack", new() { ["threadId"] = thread, ["start"] = start, ["count"] = count + 1 }, token);
        if (unpaged && page.Count > count) throw Invalid("Too many frames for an unpaged request; use startFrame/levels paging.");
        int total = Math.Max(stackTotals.GetValueOrDefault(thread), checked(start + page.Count));
        stackTotals[thread] = total;
        var result = new JArray();
        foreach (var frame in page.Take(count))
        {
            string native = (string)frame["frameId"]!;
            int id = frames.FirstOrDefault(entry => entry.Value == native).Key;
            if (id == 0) frames[id = Handle()] = native;
            var item = new JObject { ["id"] = id, ["name"] = (string?)frame["methodName"] ?? "Managed frame", ["line"] = 0, ["column"] = 0 };
            if (frame["sourceLocation"] is JObject location)
            {
                item["source"] = Source(location);
                item["line"] = (int)location["line"]! - (linesStartAt1 ? 0 : 1);
                item["column"] = Math.Max(1, (int?)location["column"] ?? 1) - (columnsStartAt1 ? 0 : 1);
            }
            result.Add(item);
        }
        // DAP explicitly permits monotonically increasing totalFrames hints for lazy stacks.
        return new() { ["stackFrames"] = result, ["totalFrames"] = total };
    }

    private JObject Scopes(JObject args)
    {
        if (!frames.TryGetValue(Integer(args, "frameId"), out var frame)) throw Invalid("Frame is stale or unknown; request stackTrace again.");
        int id = VariableNumber(new(frame, null, false));
        return new() { ["scopes"] = new JArray(new JObject { ["name"] = "Arguments, locals and static fields", ["variablesReference"] = id, ["expensive"] = false }) };
    }

    private int VariableNumber(VariableHandle value)
    {
        int id = variables.FirstOrDefault(entry => entry.Value == value).Key;
        if (id == 0) variables[id = Handle()] = value;
        return id;
    }

    private async Task<JObject> Variables(JObject args, CancellationToken token)
    {
        if (!variables.TryGetValue(Integer(args, "variablesReference"), out var handle)) throw Invalid("Variable reference is stale or unknown; request scopes again.");
        int start = Integer(args, "start", 0), count = Integer(args, "count", 0);
        if (start < 0 || count < 0) throw Invalid("Invalid variable page.");
        if (count > 1024) throw Invalid("Variable pages are limited to 1024 items; use start/count paging.");
        if (count == 0 && handle.Total is { } totalMembers)
        {
            if ((long)totalMembers - start > 1024) throw Invalid("Too many variables for an unpaged request; use start/count paging.");
            count = Math.Max(1, totalMembers - start);
        }
        bool unpagedRoot = count == 0;
        if (unpagedRoot) count = 1024;
        string? filter = (string?)args["filter"];
        if (filter is not null and not "indexed" and not "named") throw Invalid("Invalid variable filter.");
        var result = new JArray();
        if (filter is not null && (filter == "indexed") != handle.Indexed) return new() { ["variables"] = result };
        var query = new JObject { ["frameId"] = handle.Frame, ["start"] = start, ["count"] = count, ["maxDepth"] = 0 };
        if (handle.Reference is not null) query["referenceId"] = handle.Reference;
        var values = await Invoke("variables", query, token);
        if (unpagedRoot && values.Count() == 1024)
            throw Invalid("Root scope exceeds an unpaged response budget; use start/count paging.");
        foreach (var value in values)
        {
            bool indexed = System.Text.RegularExpressions.Regex.IsMatch((string?)value["typeName"] ?? "", @"\[[,]*\]$");
            int total = (int?)value["totalMembers"] ?? 0;
            int reference = total > 0 && value["referenceId"]?.Type == JTokenType.String ? VariableNumber(new(handle.Frame, (string)value["referenceId"]!, indexed, total)) : 0;
            string display = (string?)value["displayValue"] ?? "<" + ((string?)value["status"] ?? "unavailable") + ">";
            var item = new JObject { ["name"] = value["name"]!.DeepClone(), ["value"] = display, ["type"] = (string?)value["typeName"] ?? "unavailable", ["variablesReference"] = reference };
            if (reference > 0) item[indexed ? "indexedVariables" : "namedVariables"] = total;
            result.Add(item);
        }
        return new() { ["variables"] = result };
    }
}
