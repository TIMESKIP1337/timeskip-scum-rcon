using System.Buffers.Binary;
using System.Text;

namespace ScumRcon.Protocol;

/// <summary>Packet types of the Source RCON protocol.</summary>
public static class PacketType
{
    /// <summary>Server reply carrying command output.</summary>
    public const int ResponseValue = 0;

    /// <summary>Server reply to an Auth packet (id -1 means failure).</summary>
    public const int AuthResponse = 2;

    /// <summary>Client request to run a command (same number as AuthResponse, by design of the protocol).</summary>
    public const int ExecCommand = 2;

    /// <summary>Client request to authenticate.</summary>
    public const int Auth = 3;
}

/// <summary>Thrown when bytes on the wire are not a valid RCON packet.</summary>
public sealed class RconProtocolException : Exception
{
    public RconProtocolException(string message) : base(message) { }
}

/// <summary>
/// One RCON packet. Wire format (little endian):
/// int32 size, int32 id, int32 type, body bytes, 0x00, 0x00.
/// <c>size</c> counts everything after itself: 4 + 4 + body + 2.
/// </summary>
public readonly record struct RconPacket(int Id, int Type, string Body)
{
    /// <summary>Largest value of the size field we accept.</summary>
    public const int MaxPacketSize = 4096;

    /// <summary>Smallest valid size field (id + type + two terminators).</summary>
    public const int MinPacketSize = 10;

    /// <summary>Largest body that still fits in one packet.</summary>
    public const int MaxBodyBytes = MaxPacketSize - MinPacketSize;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Encodes the packet for sending.</summary>
    public byte[] Serialize()
    {
        byte[] body = Encoding.UTF8.GetBytes(Body);
        if (body.Length > MaxBodyBytes)
            throw new ArgumentException("Body is too large for one packet. Split it first.", nameof(Body));

        int size = MinPacketSize + body.Length;
        byte[] buffer = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), Id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), Type);
        body.CopyTo(buffer, 12);
        // the last two bytes are already 0x00
        return buffer;
    }

    /// <summary>
    /// Reads one packet. Returns null if the peer closed the connection cleanly
    /// before sending anything. Throws <see cref="RconProtocolException"/> for
    /// malformed or oversized data, and the size is checked BEFORE any large
    /// buffer is allocated.
    /// </summary>
    public static async Task<RconPacket?> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new RconProtocolException("Connection ended inside a packet header.");

        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < MinPacketSize || size > MaxPacketSize)
            throw new RconProtocolException($"Packet size {size} is outside {MinPacketSize}..{MaxPacketSize}.");

        byte[] payload = new byte[size];
        if (await ReadFullyAsync(stream, payload, ct).ConfigureAwait(false) < size)
            throw new RconProtocolException("Connection ended inside a packet.");

        if (payload[size - 1] != 0 || payload[size - 2] != 0)
            throw new RconProtocolException("Packet is not terminated correctly.");

        int id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        byte[] bodyBytes = payload[8..(size - 2)];

        if (Array.IndexOf(bodyBytes, (byte)0) >= 0)
            throw new RconProtocolException("Body contains a NUL byte.");

        string body;
        try
        {
            body = StrictUtf8.GetString(bodyBytes);
        }
        catch (ArgumentException)
        {
            throw new RconProtocolException("Body is not valid UTF-8.");
        }

        return new RconPacket(id, type, body);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>Splits long command output into packet-sized pieces without cutting a character in half.</summary>
public static class ResponseSplitter
{
    /// <summary>Returns the pieces. Empty text yields a single empty piece (what real servers send).</summary>
    public static IEnumerable<string> Split(string text, int maxBytes = 4000)
    {
        if (maxBytes < 4) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var current = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes)
            {
                yield return current.ToString();
                current.Clear();
                bytes = 0;
            }
            current.Append(rune.ToString());
            bytes += n;
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
