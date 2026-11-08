using System.Buffers.Binary;
using System.Text;
using ScumRcon.Protocol;

namespace ScumRcon.Server.Tests;

public class ProtocolTests
{
    private static Task<RconPacket?> Read(byte[] bytes) =>
        RconPacket.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

    [Fact]
    public async Task Packet_RoundTrips()
    {
        var original = new RconPacket(42, PacketType.ExecCommand, "ListPlayers สวัสดี");
        RconPacket? read = await Read(original.Serialize());
        Assert.Equal(original, read);
    }

    [Fact]
    public void Serialize_HasCorrectLayout()
    {
        byte[] bytes = new RconPacket(7, PacketType.Auth, "ab").Serialize();
        Assert.Equal(4 + 4 + 4 + 2 + 2, bytes.Length);
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal((byte)'a', bytes[12]);
        Assert.Equal(0, bytes[^1]);
        Assert.Equal(0, bytes[^2]);
    }

    [Fact]
    public async Task EmptyStream_ReturnsNull()
    {
        Assert.Null(await Read(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public async Task BadSize_IsRejected_BeforeAllocating(int size)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, size);
        await Assert.ThrowsAsync<RconProtocolException>(() => Read(header));
    }

    [Fact]
    public async Task TruncatedPacket_IsRejected()
    {
        byte[] full = new RconPacket(1, PacketType.ExecCommand, "hello").Serialize();
        await Assert.ThrowsAsync<RconProtocolException>(() => Read(full[..^3]));
        await Assert.ThrowsAsync<RconProtocolException>(() => Read(full[..2]));
    }

    [Fact]
    public async Task MissingTerminator_IsRejected()
    {
        byte[] bytes = new RconPacket(1, PacketType.ExecCommand, "hello").Serialize();
        bytes[^1] = 1;
        await Assert.ThrowsAsync<RconProtocolException>(() => Read(bytes));
    }

    [Fact]
    public async Task NulInsideBody_IsRejected()
    {
        byte[] bytes = new RconPacket(1, PacketType.ExecCommand, "abcd").Serialize();
        bytes[13] = 0; // a NUL in the middle of the body
        await Assert.ThrowsAsync<RconProtocolException>(() => Read(bytes));
    }

    [Fact]
    public async Task InvalidUtf8_IsRejected()
    {
        byte[] bytes = new RconPacket(1, PacketType.ExecCommand, "abcd").Serialize();
        bytes[12] = 0xFF;
        bytes[13] = 0xFE;
        await Assert.ThrowsAsync<RconProtocolException>(() => Read(bytes));
    }

    [Fact]
    public void Serialize_RefusesBodyThatDoesNotFit()
    {
        var packet = new RconPacket(1, 0, new string('a', RconPacket.MaxBodyBytes + 1));
        Assert.Throws<ArgumentException>(() => packet.Serialize());
    }

    [Fact]
    public void Splitter_EmptyTextGivesOneEmptyPiece()
    {
        Assert.Equal(new[] { string.Empty }, ResponseSplitter.Split(string.Empty).ToArray());
    }

    [Fact]
    public void Splitter_KeepsAllTextAndRespectsByteLimit_WithMultiByteCharacters()
    {
        string text = string.Concat(Enumerable.Repeat("สวัสดี🙂", 1000));
        string[] pieces = ResponseSplitter.Split(text, 100).ToArray();

        Assert.True(pieces.Length > 1);
        Assert.All(pieces, p => Assert.True(Encoding.UTF8.GetByteCount(p) <= 100));
        Assert.Equal(text, string.Concat(pieces));
    }
}
