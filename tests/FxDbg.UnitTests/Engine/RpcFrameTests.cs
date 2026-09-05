using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FxDbg.Engine.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FxDbg.UnitTests.Engine;

public sealed class RpcFrameTests
{
    [Fact]
    public async Task Frames_round_trip_without_delimiter_ambiguity()
    {
        using var stream = new MemoryStream();
        var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = "abc", ["result"] = "中文\nline\0tail" };
        await RpcFrame.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        Assert.True(JToken.DeepEquals(message, await RpcFrame.ReadAsync(stream, CancellationToken.None)));
        Assert.Null(await RpcFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_and_truncated_frames_are_rejected_before_parsing()
    {
        using var oversized = new MemoryStream(new byte[] { 255, 255, 255, 127 });
        await Assert.ThrowsAsync<InvalidDataException>(() => RpcFrame.ReadAsync(oversized, CancellationToken.None));
        using var truncated = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => RpcFrame.ReadAsync(truncated, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_fields_and_nonobject_payloads_are_rejected()
    {
        foreach (string json in new[] { "{\"id\":1,\"id\":2}", "[]" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using var stream = new MemoryStream();
            stream.Write(System.BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
            stream.Position = 0;
            await Assert.ThrowsAnyAsync<Newtonsoft.Json.JsonException>(() => RpcFrame.ReadAsync(stream, CancellationToken.None));
        }
    }
}
