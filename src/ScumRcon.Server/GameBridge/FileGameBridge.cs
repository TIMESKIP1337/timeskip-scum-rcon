using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScumRcon.GameBridge;

/// <summary>
/// Talks to a game-side script that watches two folders (see docs/FILE-BRIDGE.md).
///
/// To run a command it writes <c>&lt;id&gt;.json</c> into the commands folder and waits for
/// <c>&lt;id&gt;.json</c> to appear in the responses folder:
///   request : {"command":"ListPlayers","id":"1731070000000001"}
///   response: {"id":"1731070000000001","response":"...","success":true}
///
/// The game script reads the file with simple text patterns, so the request is built BY HAND in
/// exactly that shape. A JSON library would escape characters (for example ' as ') in a way
/// the script does not undo. For the same reason commands containing a double quote or a
/// backslash are refused instead of being sent in a damaged form.
///
/// Security: unlike the TCP bridge there is no secret. Whoever can write into the commands
/// folder can run admin commands, so only the RCON server's account (and administrators)
/// should have write access to it.
/// </summary>
public sealed class FileGameBridge : IGameBridge, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly TimeSpan StaleResponseAge = TimeSpan.FromHours(1);

    private readonly string _commandsDir;
    private readonly string _responsesDir;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _poll;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    public FileGameBridge(string commandsDirectory, string responsesDirectory, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        _commandsDir = commandsDirectory;
        _responsesDir = responsesDirectory;
        _timeout = timeout;
        _poll = pollInterval ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>Returns why a command cannot be sent through the file format, or null when it can.</summary>
    public static string? CheckCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "Empty command.";
        foreach (char c in command)
        {
            if (c is '"' or '\\')
                return "The file bridge cannot carry double quotes or backslashes in a command.";
            if (char.IsControl(c))
                return "Command contains a control character.";
        }
        return null;
    }

    public async Task<BridgeResult> ExecuteAsync(string command, CancellationToken ct)
    {
        string? problem = CheckCommand(command);
        if (problem is not null) return new BridgeResult(false, problem);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PurgeStaleResponses();

            string id = NextId();
            string commandPath = Path.Combine(_commandsDir, id + ".json");
            string tempPath = Path.Combine(_commandsDir, id + ".tmp");
            string responsePath = Path.Combine(_responsesDir, id + ".json");

            try
            {
                // Write under a name the game ignores, then rename, so it never reads half a file.
                string json = "{\"command\":\"" + command + "\",\"id\":\"" + id + "\"}";
                await File.WriteAllTextAsync(tempPath, json, Utf8NoBom, ct).ConfigureAwait(false);
                File.Move(tempPath, commandPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(tempPath);
                return new BridgeResult(false, "Cannot write to the game's command folder.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            try
            {
                while (true)
                {
                    BridgeResult? answer = TryReadResponse(responsePath, id);
                    if (answer is not null) return answer.Value;
                    await Task.Delay(_poll, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                bool notPickedUp = File.Exists(commandPath);
                TryDelete(commandPath); // never leave a command behind that could run later
                return new BridgeResult(false, notPickedUp
                    ? "The game did not pick up the command. Is the game server running with the ScumRcon mod loaded?"
                    : "The game took the command but sent no answer in time.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads and removes the answer once it is complete. Null means: not there yet.</summary>
    private BridgeResult? TryReadResponse(string path, string id)
    {
        if (!File.Exists(path)) return null;

        string text;
        try
        {
            // Open generously so a file the game is still writing can be read (it then fails to
            // parse and we simply try again on the next poll).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8NoBom);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out JsonElement replyId) || replyId.GetString() != id)
            {
                TryDelete(path);
                return new BridgeResult(false, "The game's answer did not match the request.");
            }

            bool ok = root.TryGetProperty("success", out JsonElement s) && s.ValueKind == JsonValueKind.True;
            string reply = root.TryGetProperty("response", out JsonElement r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? string.Empty
                : string.Empty;

            TryDelete(path);
            if (!ok && reply == "Failed to execute")
                reply += " (the game side may need at least one player connected)";
            return new BridgeResult(ok, reply);
        }
        catch (JsonException)
        {
            return null; // incomplete file; try again on the next poll
        }
    }

    private string NextId()
    {
        long millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long n = Interlocked.Increment(ref _sequence) % 1000;
        return millis.ToString(CultureInfo.InvariantCulture) + n.ToString("D3", CultureInfo.InvariantCulture);
    }

    /// <summary>Removes answers nobody collected (for example after a timeout). Only our own file names.</summary>
    private void PurgeStaleResponses()
    {
        try
        {
            DateTime limit = DateTime.UtcNow - StaleResponseAge;
            foreach (string file in Directory.EnumerateFiles(_responsesDir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 0 && name.All(char.IsAsciiDigit) && File.GetLastWriteTimeUtc(file) < limit)
                    TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // folder missing or locked: nothing to clean
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>Creates the bridge selected in the configuration.</summary>
public static class GameBridgeFactory
{
    public static IGameBridge Create(Config.BridgeConfig config)
    {
        if (!config.TryGetMode(out Config.BridgeMode mode))
            throw new ArgumentException("Unknown bridge mode.", nameof(config));

        return mode == Config.BridgeMode.File
            ? new FileGameBridge(config.CommandsDirectory, config.ResponsesDirectory, TimeSpan.FromSeconds(config.TimeoutSeconds))
            : new TcpGameBridge(config);
    }
}
