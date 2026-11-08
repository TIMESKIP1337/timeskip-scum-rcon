using System.Globalization;
using System.Net;

namespace ScumRcon.Security;

/// <summary>
/// Which client addresses may connect. Entries are single addresses or CIDR
/// ranges ("203.0.113.5", "10.0.0.0/8", "::1"). An EMPTY list allows nobody:
/// it fails closed, not open.
/// </summary>
public sealed class IpAllowlist
{
    private readonly (byte[] Network, int Prefix)[] _rules;

    /// <summary>Number of rules.</summary>
    public int Count => _rules.Length;

    /// <summary>Builds the list. Throws <see cref="FormatException"/> for a bad entry.</summary>
    public IpAllowlist(IEnumerable<string> entries)
    {
        var rules = new List<(byte[], int)>();
        foreach (string? raw in entries)
        {
            string entry = raw?.Trim() ?? string.Empty;
            if (entry.Length == 0) throw new FormatException("Empty allowlist entry.");

            string[] parts = entry.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out IPAddress? ip))
                throw new FormatException($"Invalid allowlist entry: {entry}");

            byte[] bytes = Normalize(ip).GetAddressBytes();
            int max = bytes.Length * 8;
            int prefix = max;
            if (parts.Length == 2 &&
                (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > max))
            {
                throw new FormatException($"Invalid prefix length in allowlist entry: {entry}");
            }
            rules.Add((bytes, prefix));
        }
        _rules = rules.ToArray();
    }

    /// <summary>True when the address matches at least one rule.</summary>
    public bool IsAllowed(IPAddress address)
    {
        byte[] candidate = Normalize(address).GetAddressBytes();
        foreach (var (network, prefix) in _rules)
        {
            if (network.Length == candidate.Length && PrefixMatches(network, candidate, prefix))
                return true;
        }
        return false;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static bool PrefixMatches(byte[] a, byte[] b, int prefix)
    {
        int fullBytes = prefix / 8;
        for (int i = 0; i < fullBytes; i++)
            if (a[i] != b[i]) return false;

        int rest = prefix % 8;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
