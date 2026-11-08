namespace ScumRcon.Security;

/// <summary>Access level of a user. Higher includes everything lower.</summary>
public enum Role
{
    Viewer = 0,
    Moderator = 1,
    Admin = 2
}

/// <summary>Result of checking a command against the policy.</summary>
public readonly record struct PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow() => new(true, string.Empty);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides who may run which command. Rules:
///  - a command is its first word, case-insensitive, without a leading '#';
///  - every command has a minimum role; commands NOT listed need Admin
///    (unknown means restricted, not open);
///  - control characters are refused, so a command cannot smuggle a second
///    line to the game; and length is capped.
/// </summary>
public sealed class CommandPolicy
{
    private readonly Dictionary<string, Role> _minimumRoles;
    private readonly int _maxLength;

    public CommandPolicy(IReadOnlyDictionary<string, Role> minimumRoles, int maxLength = 512)
    {
        if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));
        _maxLength = maxLength;
        _minimumRoles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in minimumRoles)
            _minimumRoles[Normalize(pair.Key)] = pair.Value;
    }

    /// <summary>Extracts the command word used for lookups.</summary>
    public static string CommandWord(string command)
    {
        string trimmed = command.Trim();
        int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
        string word = space < 0 ? trimmed : trimmed[..space];
        return Normalize(word);
    }

    private static string Normalize(string word) => word.TrimStart('#').ToLowerInvariant();

    /// <summary>Checks one command for one role.</summary>
    public PolicyDecision Check(Role role, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return PolicyDecision.Deny("Empty command.");
        if (command.Length > _maxLength)
            return PolicyDecision.Deny($"Command is longer than {_maxLength} characters.");

        foreach (char c in command)
        {
            if (char.IsControl(c))
                return PolicyDecision.Deny("Command contains a control character.");
        }

        string word = CommandWord(command);
        if (word.Length == 0)
            return PolicyDecision.Deny("Empty command.");

        Role required = _minimumRoles.TryGetValue(word, out Role r) ? r : Role.Admin;
        return role >= required
            ? PolicyDecision.Allow()
            : PolicyDecision.Deny($"'{word}' needs the {required} role.");
    }
}
