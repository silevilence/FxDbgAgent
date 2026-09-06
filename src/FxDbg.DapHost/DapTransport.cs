using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FxDbg.DapHost;

internal sealed class DapTransport(Stream input, Stream output)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim writing = new(1, 1);
    private int sequence;

    internal async Task<JObject?> ReadAsync(CancellationToken token)
    {
        var header = new List<byte>();
        byte[] one = new byte[1];
        while (true)
        {
            int count = await input.ReadAsync(one, token);
            if (count == 0) return header.Count == 0 ? null : throw new InvalidDataException("Incomplete DAP header.");
            header.Add(one[0]);
            if (header.Count > 8192) throw new InvalidDataException("DAP header exceeds 8 KiB.");
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        int? length = null;
        foreach (string line in Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 1) throw new InvalidDataException("Malformed DAP header.");
            if (!line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (length.HasValue || !int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1 || parsed > MaximumBytes)
                throw new InvalidDataException("DAP content length is invalid.");
            length = parsed;
        }
        if (!length.HasValue) throw new InvalidDataException("DAP Content-Length is required.");
        byte[] body = new byte[length.Value];
        await input.ReadExactlyAsync(body, token);
        using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(body))) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        var packet = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Trailing DAP JSON data.");
        return packet;
    }

    internal async Task SendAsync(JObject packet, CancellationToken token)
    {
        await writing.WaitAsync(token);
        try
        {
            packet["seq"] = checked(++sequence);
            byte[] data = Encoding.UTF8.GetBytes(packet.ToString(Formatting.None));
            if (data.Length > MaximumBytes) throw new ArgumentException("Result exceeds 4 MiB; request a smaller page.");
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {data.Length}\r\n\r\n"), token);
            await output.WriteAsync(data, token);
            await output.FlushAsync(token);
        }
        finally { writing.Release(); }
    }
}
