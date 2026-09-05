using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FxDbg.Engine.Protocol;

/// <summary>Unsigned little-endian byte length followed by one UTF-8 JSON object.</summary>
public static class RpcFrame
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static async Task<JObject?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        int first = await stream.ReadAsync(header, 0, 4, cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadRemaining(stream, header, first, cancellationToken).ConfigureAwait(false);
        long length = header[0] | ((long)header[1] << 8) | ((long)header[2] << 16) | ((long)header[3] << 24);
        if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("RPC frame length exceeds its 4 MiB limit.");
        var bytes = new byte[(int)length];
        await ReadRemaining(stream, bytes, 0, cancellationToken).ConfigureAwait(false);
        using var text = new StringReader(Utf8.GetString(bytes));
        using var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        JObject value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new JsonReaderException("RPC frame contains trailing JSON.");
        return value;
    }

    public static async Task WriteAsync(Stream stream, JObject value, CancellationToken cancellationToken)
    {
        byte[] bytes = Utf8.GetBytes(value.ToString(Formatting.None));
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("RPC frame length exceeds its 4 MiB limit.");
        int length = bytes.Length;
        var header = new[] { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) };
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadRemaining(Stream stream, byte[] bytes, int offset, CancellationToken token)
    {
        while (offset < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("RPC frame was truncated.");
            offset += count;
        }
    }
}
