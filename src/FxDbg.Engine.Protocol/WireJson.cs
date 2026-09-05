using System;
using FxDbg.Core.Model;
using FxDbg.Core.Sessions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace FxDbg.Engine.Protocol;

public static class WireJson
{
    public const int ProtocolVersion = 1;

    public static JsonSerializer CreateSerializer() => JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        TypeNameHandling = TypeNameHandling.None,
        MaxDepth = 64,
        Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()), new IdentifierConverter() }
    });

    public static JToken Value(object? value) => value is null ? JValue.CreateNull() : JToken.FromObject(value, CreateSerializer());

    private sealed class IdentifierConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(SessionId) || type == typeof(FrameId) || type == typeof(BreakpointId) || type == typeof(VariableReferenceId);
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => writer.WriteValue(value?.ToString());
        public override object? ReadJson(JsonReader reader, Type type, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            if (reader.TokenType != JsonToken.String) throw new JsonSerializationException("Identifier must be a string.");
            string value = (string)reader.Value!;
            if (type == typeof(SessionId)) return new SessionId(Guid.Parse(value));
            if (type == typeof(FrameId)) return new FrameId(value);
            if (type == typeof(BreakpointId)) return new BreakpointId(value);
            return new VariableReferenceId(value);
        }
    }
}
