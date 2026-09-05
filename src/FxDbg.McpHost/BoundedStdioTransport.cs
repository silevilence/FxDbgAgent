using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FxDbg.McpHost;

/// <summary>Only framing and backpressure; the official SDK owns MCP dispatch and JSON-RPC types.</summary>
internal sealed class BoundedStdioTransport : ITransport
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private readonly Stream input;
    private readonly Stream output;
    private readonly Channel<JsonRpcMessage> incoming = Channel.CreateBounded<JsonRpcMessage>(16);
    private readonly Channel<Outgoing> outgoing = Channel.CreateBounded<Outgoing>(8);
    private readonly CancellationTokenSource closed = new();
    private readonly CancellationTokenSource disconnected = new();
    private readonly SemaphoreSlim dispatched = new(64, 64);
    private readonly Task reader;
    private readonly Task writer;
    private int disposed;
    internal BoundedStdioTransport(Stream input, Stream output)
    {
        this.input = input; this.output = output;
        reader = ReadAsync(); writer = WriteAsync();
    }
    public string? SessionId => null;
    public ChannelReader<JsonRpcMessage> MessageReader => incoming.Reader;
    internal CancellationToken Disconnected => disconnected.Token;
    internal bool Failed { get; private set; }
    internal void MessageHandled() => dispatched.Release();

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, McpJsonUtilities.DefaultOptions);
        if (bytes.Length > MaximumBytes) { Close(new InvalidDataException("MCP output exceeds 4 MiB.")); throw new IOException("MCP output exceeds 4 MiB."); }
        var item = new Outgoing(bytes);
        if (!outgoing.Writer.TryWrite(item)) { Close(new IOException("MCP output capacity exceeded.")); throw new IOException("MCP output capacity exceeded."); }
        return item.Completion.Task;
    }

    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[8192];
            using var line = new MemoryStream();
            while (!closed.IsCancellationRequested)
            {
                int read = await input.ReadAsync(buffer, closed.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (line.Length != 0) throw new InvalidDataException("MCP input ended inside a frame.");
                    incoming.Writer.TryComplete();
                    disconnected.Cancel();
                    return;
                }
                int begin = 0;
                for (int index = 0; index < read; index++)
                {
                    if (buffer[index] != (byte)'\n') continue;
                    Append(buffer.AsSpan(begin, index - begin));
                    using var document = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length), new JsonDocumentOptions { MaxDepth = 64 });
                    ValidateObject(document.RootElement);
                    if (document.RootElement.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("Invalid JSON-RPC version.");
                    var message = document.RootElement.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions) ?? throw new InvalidDataException("Invalid MCP frame.");
                    // The SDK yields each handler. Bound those not-yet-completed handlers too,
                    // not merely the transport channel that the SDK can drain immediately.
                    await dispatched.WaitAsync(closed.Token).ConfigureAwait(false);
                    await incoming.Writer.WriteAsync(message, closed.Token).ConfigureAwait(false);
                    line.SetLength(0); begin = index + 1;
                }
                Append(buffer.AsSpan(begin, read - begin));
            }
            void Append(ReadOnlySpan<byte> bytes)
            {
                if (line.Length + bytes.Length > MaximumBytes) throw new InvalidDataException("MCP input exceeds 4 MiB.");
                line.Write(bytes);
            }
        }
        catch (Exception error) { Close(error); }
    }

    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) ValidateObject(item);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                ValidateObject(property.Value);
            }
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (Outgoing item in outgoing.Reader.ReadAllAsync(closed.Token).ConfigureAwait(false))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    using var abort = deadline.Token.Register(() => { if (!closed.IsCancellationRequested) Close(new IOException("MCP output stalled.")); });
                    await output.WriteAsync(item.Bytes, deadline.Token).ConfigureAwait(false);
                    await output.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token).ConfigureAwait(false);
                    await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult();
                }
                catch (Exception error) { item.Completion.TrySetException(error); throw; }
            }
        }
        catch (Exception error) { Close(error); }
        finally { while (outgoing.Reader.TryRead(out Outgoing? item)) item.Completion.TrySetException(new IOException("MCP transport closed.")); }
    }

    private void Close(Exception? error)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Failed = error is not null;
        incoming.Writer.TryComplete(error); outgoing.Writer.TryComplete(error);
        disconnected.Cancel();
        closed.Cancel();
        input.Dispose(); output.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        Close(null);
        // Console input on Windows can retain a synchronous pending ReadFile after
        // cancellation/handle disposal. It must not keep process shutdown waiting on the client.
        try { await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
    private sealed class Outgoing(byte[] bytes)
    {
        internal byte[] Bytes { get; } = bytes;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
