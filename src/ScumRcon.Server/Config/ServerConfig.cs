using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScumRcon.Security;

namespace ScumRcon.Config;

/// <summary>One account that may log in over RCON.</summary>
public sealed class UserConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Output of <see cref="PasswordHasher.Hash"/>. Never a plain password.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Viewer, Moderator or Admin.</summary>
    public string Role { get; set; } = "Viewer";
}

/// <summary>How the RCON server reaches the game.</summary>
public enum BridgeMode
{
    /// <summary>Authenticated loopback TCP (see docs/BRIDGE-PROTOCOL.md).</summary>
    Tcp,

    /// <summary>Command and answer files in two folders watched by the game (see docs/FILE-BRIDGE.md).</summary>
    File
}

/// <summary>How to reach the in-game side of the system.</summary>
public sealed class BridgeConfig
{
    /// <summary>"Tcp" or "File".</summary>
    public string Mode { get; set; } = "Tcp";

    // ---- Tcp mode -----------------------------------------------------

    /// <summary>Must be a loopback address (127.0.0.1 or ::1).</summary>
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 28016;

    /// <summary>Shared secret the game side checks on every message. Generated on first run.</summary>
    public string Secret { get; set; } = string.Empty;

    // ---- File mode ----------------------------------------------------

    /// <summary>Absolute path of the folder the game watches for command files.</summary>
    public string CommandsDirectory { get; set; } = string.Empty;

    /// <summary>Absolute path of the folder the game writes its answers to.</summary>
    public string ResponsesDirectory { get; set; } = string.Empty;

    // ---- both ---------------------------------------------------------

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Parses <see cref="Mode"/>. False for anything but Tcp or File (case-insensitive).</summary>
    public bool TryGetMode(out BridgeMode mode)
    {
        mode = BridgeMode.Tcp;
        if (string.Equals(Mode?.Trim(), "Tcp", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(Mode?.Trim(), "File", StringComparison.OrdinalIgnoreCase))
        {
            mode = BridgeMode.File;
            return true;
        }
        return false;
    }
}

/// <summary>All settings of the RCON server.</summary>
public sealed class ServerConfig
{
    /// <summary>Address to listen on. Loopback by default so a fresh install is not exposed.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 28015;

    /// <summary>Who may connect. Empty means nobody.</summary>
    public List<string> IpAllowlist { get; set; } = new() { "127.0.0.1", "::1" };

    public int MaxConnections { get; set; } = 10;
    public int AuthTimeoutSeconds { get; set; } = 15;
    public int IdleTimeoutSeconds { get; set; } = 300;
    public int MaxFailedAuth { get; set; } = 5;
    public int FailureWindowMinutes { get; set; } = 10;
    public int BlockMinutes { get; set; } = 15;
    public int MaxCommandsPerSecond { get; set; } = 5;
    public int MaxCommandLength { get; set; } = 512;

    public List<UserConfig> Users { get; set; } = new();

    /// <summary>Minimum role per command word. Commands not listed need Admin.</summary>
    public Dictionary<string, string> CommandRoles { get; set; } = DefaultCommandRoles();

    public BridgeConfig Bridge { get; set; } = new();
    public string AuditLogPath { get; set; } = "logs/audit.jsonl";

    public static Dictionary<string, string> DefaultCommandRoles() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["help"] = "Viewer",
        ["ListPlayers"] = "Viewer",
        ["players"] = "Viewer"
    };
}

/// <summary>Reading, writing and checking the configuration file.</summary>
public static class ConfigLoader
{
    private static readonly Regex UserName = new("^[A-Za-z0-9_.-]{1,32}$", RegexOptions.Compiled);
    private static readonly string[] PlaceholderWords = { "change", "example", "secret", "password", "todo", "xxxx" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static ServerConfig Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ServerConfig>(stream, Json)
               ?? throw new InvalidDataException("Configuration file is empty.");
    }

    public static void Save(ServerConfig config, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
    }

    /// <summary>
    /// Builds a first-run configuration with a random admin password and a
    /// random bridge secret. The plain password is returned ONCE so it can be
    /// shown to the owner; only its hash is stored.
    /// </summary>
    public static ServerConfig CreateFirstRun(out string adminPassword)
    {
        adminPassword = GeneratePassword(24);
        var config = new ServerConfig();
        config.Users.Add(new UserConfig
        {
            Name = "admin",
            Role = "Admin",
            PasswordHash = PasswordHasher.Hash(adminPassword)
        });
        config.Bridge.Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return config;
    }

    private static string GeneratePassword(int length)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Parses a role name. Numbers and unknown words are refused.</summary>
    public static bool TryParseRole(string? text, out Role role)
    {
        role = Role.Viewer;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string name in Enum.GetNames<Role>())
        {
            if (string.Equals(name, text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                role = Enum.Parse<Role>(name);
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns every problem found. An empty list means the server may start.</summary>
    public static IReadOnlyList<string> Validate(ServerConfig c)
    {
        var errors = new List<string>();

        if (!IPAddress.TryParse(c.ListenAddress, out IPAddress? listen))
            errors.Add($"listenAddress '{c.ListenAddress}' is not an IP address.");
        if (c.Port is < 1 or > 65535) errors.Add("port must be 1..65535.");
        if (c.MaxConnections < 1) errors.Add("maxConnections must be at least 1.");
        if (c.AuthTimeoutSeconds < 1) errors.Add("authTimeoutSeconds must be at least 1.");
        if (c.IdleTimeoutSeconds < 1) errors.Add("idleTimeoutSeconds must be at least 1.");
        if (c.MaxFailedAuth < 1) errors.Add("maxFailedAuth must be at least 1.");
        if (c.FailureWindowMinutes < 1) errors.Add("failureWindowMinutes must be at least 1.");
        if (c.BlockMinutes < 1) errors.Add("blockMinutes must be at least 1.");
        if (c.MaxCommandsPerSecond < 1) errors.Add("maxCommandsPerSecond must be at least 1.");
        if (c.MaxCommandLength is < 1 or > 2048) errors.Add("maxCommandLength must be 1..2048.");

        try
        {
            var list = new IpAllowlist(c.IpAllowlist);
            bool openToWorld = c.IpAllowlist.Any(e => e.Trim() is "0.0.0.0/0" or "::/0");
            if (openToWorld)
                errors.Add("ipAllowlist must not allow the whole internet (0.0.0.0/0 or ::/0).");
            if (listen is not null && !IPAddress.IsLoopback(listen) && list.Count == 0)
                errors.Add("Listening on a non-loopback address needs a non-empty ipAllowlist.");
        }
        catch (FormatException ex)
        {
            errors.Add(ex.Message);
        }

        if (c.Users.Count == 0) errors.Add("At least one user is required.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UserConfig u in c.Users)
        {
            if (!UserName.IsMatch(u.Name ?? string.Empty))
                errors.Add($"User name '{u.Name}' must be 1-32 letters, digits, '_', '.' or '-'.");
            else if (!seen.Add(u.Name!))
                errors.Add($"User '{u.Name}' is listed twice.");
            if (!PasswordHasher.IsValidFormat(u.PasswordHash))
                errors.Add($"User '{u.Name}' has no valid passwordHash. Use --hash-password to create one.");
            if (!TryParseRole(u.Role, out _))
                errors.Add($"User '{u.Name}' has unknown role '{u.Role}'.");
        }

        foreach (var pair in c.CommandRoles)
        {
            if (!TryParseRole(pair.Value, out _))
                errors.Add($"commandRoles['{pair.Key}'] has unknown role '{pair.Value}'.");
        }

        if (c.Bridge.TimeoutSeconds < 1) errors.Add("bridge.timeoutSeconds must be at least 1.");

        if (!c.Bridge.TryGetMode(out BridgeMode mode))
        {
            errors.Add($"bridge.mode '{c.Bridge.Mode}' must be 'Tcp' or 'File'.");
        }
        else if (mode == BridgeMode.Tcp)
        {
            string secret = c.Bridge.Secret ?? string.Empty;
            if (secret.Length < 32)
                errors.Add("bridge.secret must be at least 32 characters.");
            else if (PlaceholderWords.Any(w => secret.Contains(w, StringComparison.OrdinalIgnoreCase)))
                errors.Add("bridge.secret looks like a placeholder. Use a random value.");

            if (!IPAddress.TryParse(c.Bridge.Host, out IPAddress? bridgeHost) || !IPAddress.IsLoopback(bridgeHost))
                errors.Add("bridge.host must be a loopback address (127.0.0.1 or ::1).");
            if (c.Bridge.Port is < 1 or > 65535) errors.Add("bridge.port must be 1..65535.");
        }
        else
        {
            string commands = c.Bridge.CommandsDirectory ?? string.Empty;
            string responses = c.Bridge.ResponsesDirectory ?? string.Empty;
            if (!Path.IsPathFullyQualified(commands))
                errors.Add("bridge.commandsDirectory must be a full (absolute) path in File mode.");
            if (!Path.IsPathFullyQualified(responses))
                errors.Add("bridge.responsesDirectory must be a full (absolute) path in File mode.");
            if (commands.Length > 0 && string.Equals(
                    Path.TrimEndingDirectorySeparator(commands),
                    Path.TrimEndingDirectorySeparator(responses),
                    StringComparison.OrdinalIgnoreCase))
                errors.Add("bridge.commandsDirectory and bridge.responsesDirectory must be different folders.");
        }

        return errors;
    }
}
