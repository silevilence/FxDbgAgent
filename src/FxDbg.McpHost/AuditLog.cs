using System.Text.Json;
using System.Text.Json.Nodes;

namespace FxDbg.McpHost;

/// <summary>Whitelisted metadata only. Target values and exception/request text have no logging API.</summary>
internal sealed class AuditLog : IDisposable
{
    private readonly string level;
    private readonly StreamWriter? file;
    private readonly object gate = new();
    internal AuditLog(string level, string? path)
    {
        if (level is not ("off" or "error" or "info" or "debug")) throw new ArgumentException("Log level must be off, error, info or debug.");
        this.level = level;
        if (level != "off" && path is not null) file = new StreamWriter(new FileStream(Path.GetFullPath(path), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }
    internal void ToolCompleted(string command, JsonObject result)
    {
        bool ok = result["ok"]!.GetValue<bool>();
        if (level == "off" || level == "error" && ok) return;
        string? session = (string?)result["sessionId"];
        session = Guid.TryParse(session, out Guid id) ? id.ToString("D") : null;
        JsonObject? payload = result["result"] as JsonObject;
        int? pid = (int?)payload?["processId"] ?? (int?)payload?["target"]?["processId"] ?? (int?)payload?["stop"]?["processId"];
        string line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, sessionId = session, pid, command,
            state = ok ? "completed" : "failed", code = ok ? null : (string?)result["error"]?["code"],
            hostThreadId = level == "debug" ? (int?)Environment.CurrentManagedThreadId : null });
        lock (gate)
        {
            try
            {
                if (file is null) Console.Error.WriteLine(line);
                else
                {
                    // A single bounded local log, oldest entries discarded on reaching the limit.
                    if (file.BaseStream.Length + line.Length * 3 > 1024 * 1024) { file.BaseStream.SetLength(0); file.BaseStream.Position = 0; }
                    file.WriteLine(line);
                }
            }
            catch (IOException) { } // Logging cannot turn an already-executed command into a retryable failure.
        }
    }
    public void Dispose() => file?.Dispose();
}
