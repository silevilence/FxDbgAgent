using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>Owns only the MCP process; the official SDK handles the protocol over its actual stdio streams.</summary>
internal sealed class McpTestConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private bool disposed;
    private McpTestConnection(Process process)
    {
        this.process = process;
        stderr = process.StandardError.ReadToEndAsync();
    }
    internal McpClient Client { get; private set; } = null!;
    internal string StandardError { get; private set; } = "";
    internal int ProcessId => process.Id;

    internal static async Task<McpTestConnection> Create(string bundle, CancellationToken token, string? directory = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = directory ?? Path.GetTempPath()
        };
        start.ArgumentList.Add(Path.Combine(Path.GetFullPath(bundle), "fxdbg-mcp.dll"));
        var connection = new McpTestConnection(Process.Start(start)!);
        try
        {
            connection.Client = await McpClient.CreateAsync(new StreamClientTransport(connection.process.StandardInput.BaseStream, connection.process.StandardOutput.BaseStream),
                new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: token);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            // Send genuine EOF first. SDK StdioClientTransport.Dispose can KillTree; it is inappropriate for debugger lifecycle assertions.
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(); // This test owns this Host, never its entire process tree.
                throw new TimeoutException("MCP Host did not exit within fifteen seconds after stdin EOF.");
            }
            StandardError = await stderr;
        }
        finally
        {
            if (Client is not null) await Client.DisposeAsync();
            process.Dispose();
        }
    }
}
