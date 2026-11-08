using ScumRcon.GameBridge;
using ScumRcon.Logging;
using ScumRcon.Security;

namespace ScumRcon.Core;

/// <summary>Why <see cref="ConsoleShell.RunAsync"/> returned.</summary>
public enum ShellEnd
{
    /// <summary>The user typed exit or quit.</summary>
    ExitRequested,
    /// <summary>There is no more input (the window has no keyboard, or input was piped and ended).</summary>
    EndOfInput,
    /// <summary>The cancellation token was cancelled (Ctrl+C).</summary>
    Cancelled
}

/// <summary>
/// A command prompt inside the server window, so one window is enough: type a command, read the answer.
///
/// It is for the person sitting at the server machine, so it needs no login and runs as Admin. That is the
/// same trust as being able to write the bridge command files. It still refuses empty or control-character
/// commands, and every command is written to the audit log with the user name "console".
/// </summary>
public sealed class ConsoleShell
{
    public const string ConsoleUser = "console";
    public const string Prompt = "rcon> ";

    private readonly IGameBridge _bridge;
    private readonly IAuditSink _audit;
    private readonly CommandPolicy _policy;
    private readonly ConsoleTheme _theme;

    /// <param name="policy">Only the length and character checks matter here: the shell runs as Admin.</param>
    /// <param name="theme">Colours; <see cref="ConsoleTheme.Plain"/> (the default) adds nothing to the text.</param>
    public ConsoleShell(IGameBridge bridge, IAuditSink audit, CommandPolicy policy, ConsoleTheme? theme = null)
    {
        _bridge = bridge;
        _audit = audit;
        _policy = policy;
        _theme = theme ?? ConsoleTheme.Plain;
    }

    private string PromptText => _theme.Colors
        ? _theme.Bold("rcon", 0, 225, 255) + _theme.Paint("> ", 255, 90, 200)
        : Prompt;

    public async Task<ShellEnd> RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        await output.WriteLineAsync(_theme.Kind(
            "Type a game command and press Enter, for example: ListPlayers  or  say global Hello.  Type exit to stop the server.",
            LineKind.Dim)).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            await output.WriteAsync(PromptText).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);

            string? line;
            try
            {
                line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ShellEnd.Cancelled;
            }
            if (line is null) return ShellEnd.EndOfInput;

            // A byte-order mark (U+FEFF) can come with piped input; it is not part of the command.
            string command = line.TrimStart('﻿').Trim();
            if (command.Length == 0) continue;
            if (command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("quit", StringComparison.OrdinalIgnoreCase))
                return ShellEnd.ExitRequested;

            PolicyDecision decision = _policy.Check(Role.Admin, command);
            if (!decision.Allowed)
            {
                Audit("cmd_denied", command, ("reason", decision.Reason));
                await output.WriteLineAsync(_theme.Kind("Denied: " + decision.Reason, LineKind.Warning)).ConfigureAwait(false);
                continue;
            }

            BridgeResult result;
            try
            {
                result = await _bridge.ExecuteAsync(command, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ShellEnd.Cancelled;
            }
            Audit("cmd", command, ("success", result.Success));
            await output.WriteLineAsync(result.Success
                ? _theme.Kind(result.Text, LineKind.Success)
                : _theme.Kind("Error: " + result.Text, LineKind.Error)).ConfigureAwait(false);
        }
        return ShellEnd.Cancelled;
    }

    private void Audit(string eventName, string command, params (string Key, object? Value)[] extra)
    {
        var data = new Dictionary<string, object?>
        {
            ["ip"] = "local",
            ["user"] = ConsoleUser,
            ["role"] = Role.Admin.ToString(),
            ["command"] = command.Length <= 256 ? command : command[..256]
        };
        foreach (var (key, value) in extra) data[key] = value;
        _audit.Write(eventName, data);
    }
}
