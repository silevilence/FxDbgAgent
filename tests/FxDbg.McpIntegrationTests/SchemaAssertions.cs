using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

/// <summary>Checks the schema keywords emitted by this server against every real tool response.</summary>
internal static class SchemaAssertions
{
    private static readonly ConditionalWeakTable<McpClient, Dictionary<string, JsonNode>> schemas = new();
    internal static async Task Register(McpClient client, CancellationToken token)
    {
        var tools = await client.ListToolsAsync(cancellationToken: token);
        schemas.Add(client, tools.ToDictionary(x => x.Name, x => JsonNode.Parse(x.ProtocolTool.OutputSchema!.Value.GetRawText())!));
    }
    internal static void Check(McpClient client, string tool, JsonNode value)
    {
        var root = schemas.GetValue(client, _ => throw new InvalidOperationException("Client schemas not registered."))[tool];
        ObservationSuite.Require(Matches(root, value, root), "Response violates advertised " + tool + " outputSchema: " + value.ToJsonString());
    }
    private static bool Matches(JsonNode schema, JsonNode? value, JsonNode root)
    {
        if (schema["$ref"] is JsonNode reference)
            return Matches(root["$defs"]![reference.GetValue<string>().Split('/')[^1]]!, value, root);
        if (schema["const"] is JsonNode constant && !JsonNode.DeepEquals(value, constant)) return false;
        if (schema["enum"] is JsonArray choices && !choices.Any(x => JsonNode.DeepEquals(x, value))) return false;
        if (schema["anyOf"] is JsonArray any && !any.Any(x => Matches(x!, value, root))) return false;
        if (schema["oneOf"] is JsonArray one && one.Count(x => Matches(x!, value, root)) != 1) return false;
        if (schema["type"] is JsonNode type)
        {
            bool valid = type.GetValue<string>() switch
            {
                "null" => value is null,
                "object" => value is JsonObject,
                "array" => value is JsonArray,
                "string" => value?.GetValueKind() == JsonValueKind.String,
                "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
                "integer" => value?.GetValueKind() == JsonValueKind.Number && decimal.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal number) && number == decimal.Truncate(number),
                _ => throw new InvalidOperationException("Unhandled schema type.")
            };
            if (!valid) return false;
        }
        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required && required.Any(x => !obj.ContainsKey(x!.GetValue<string>()))) return false;
            if (schema["properties"] is JsonObject properties)
                foreach (var field in obj)
                {
                    if (properties.TryGetPropertyValue(field.Key, out JsonNode? fieldSchema)) { if (!Matches(fieldSchema!, field.Value, root)) return false; }
                    else if ((bool?)schema["additionalProperties"] == false) return false;
                }
        }
        if (value is JsonArray items && schema["items"] is JsonNode itemSchema && items.Any(x => !Matches(itemSchema, x, root))) return false;
        return true;
    }
}
