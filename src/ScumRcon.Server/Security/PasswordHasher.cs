using System.Globalization;
using System.Security.Cryptography;

namespace ScumRcon.Security;

/// <summary>
/// Password hashing with PBKDF2-HMAC-SHA256. Stored form:
/// <c>pbkdf2-sha256$iterations$saltBase64$hashBase64</c>.
/// Plain passwords are never stored or logged.
/// </summary>
public static class PasswordHasher
{
    /// <summary>Iteration count for new hashes (OWASP guidance for PBKDF2-SHA256).</summary>
    public const int DefaultIterations = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxIterations = 10_000_000;

    private static readonly Lazy<string> Dummy = new(() => Hash("timing-equalizer", DefaultIterations));

    /// <summary>
    /// A valid hash of a throwaway password. Verifying against it when the user
    /// does not exist keeps the response time the same as for a wrong password.
    /// </summary>
    public static string DummyHash => Dummy.Value;

    /// <summary>Creates a stored hash with a fresh random salt.</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (iterations < 1 || iterations > MaxIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Parses a stored hash. False if it is malformed or out of range.</summary>
    public static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();
        if (string.IsNullOrEmpty(stored)) return false;

        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)) return false;
        if (iterations < 1 || iterations > MaxIterations) return false;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length is >= 8 and <= 64 && hash.Length is >= 16 and <= 64;
    }

    /// <summary>True when the stored value has the expected shape.</summary>
    public static bool IsValidFormat(string? stored) => TryParse(stored, out _, out _, out _);

    /// <summary>Checks a password against a stored hash in constant time.</summary>
    public static bool Verify(string password, string? stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(stored, out int iterations, out byte[] salt, out byte[] expected)) return false;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True when the hash was made with fewer iterations than today's default.</summary>
    public static bool NeedsRehash(string? stored) =>
        TryParse(stored, out int iterations, out _, out _) && iterations < DefaultIterations;
}
