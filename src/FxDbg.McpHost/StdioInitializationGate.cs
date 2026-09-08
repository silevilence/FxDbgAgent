using System.Runtime.CompilerServices;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FxDbg.McpHost;

/// <summary>Capture wire order before the SDK independently schedules message filters.</summary>
internal sealed class StdioInitializationGate(Func<bool> clientKnown)
{
    private sealed record Admission(bool Allowed);
    private readonly ConditionalWeakTable<JsonRpcMessage, Admission> requests = new();
    private int initialized;
    internal bool IsInitialized => Volatile.Read(ref initialized) != 0;

    internal void Received(JsonRpcMessage message)
    {
        if (message is JsonRpcNotification { Method: "notifications/initialized" } && clientKnown())
            Volatile.Write(ref initialized, 1);
        if (IsToolRequest(message)) requests.Add(message, new Admission(IsInitialized && clientKnown()));
    }

    internal void RequireAllowed(JsonRpcMessage message)
    {
        if (IsToolRequest(message) && (!requests.TryGetValue(message, out Admission? admission) || !admission.Allowed))
            throw new McpProtocolException("Complete initialize and notifications/initialized first.", McpErrorCode.InvalidRequest);
    }

    private static bool IsToolRequest(JsonRpcMessage message) => message is JsonRpcRequest { Method: "tools/list" or "tools/call" };
}
