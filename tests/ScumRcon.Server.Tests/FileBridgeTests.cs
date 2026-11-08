using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ScumRcon.Config;
using ScumRcon.GameBridge;

namespace ScumRcon.Server.Tests;

/// <summary>
/// Stand-in for the game's rcon_bridge.lua. It does what the script does, including reading the
/// request with the SAME simple text patterns, so a request the real script could not read
/// fails here too.
/// </summary>
internal sealed class FakeLuaConsumer : IDisposable
{
    private static readonly Regex CommandPattern = new("\"command\":\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new("\"id\":\"([^\"]*)\"", RegexOptions.Compiled);

    private readonly string _commandsDir;
    private readonly string _responsesDir;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public readonly ConcurrentQueue<string> RawFiles = new();
    public readonly ConcurrentQueue<string> ParsedCommands = new();

    /// <summary>Returns the answer, or null to take the command and never answer.</summary>
    public Func<string, (bool Ok, string Text)?> Handler = cmd => (true, "Executed: " + cmd);

    public bool ForgeId;
    public int SlowWriteMs;

    public FakeLuaConsumer(string commandsDir, string responsesDir)
    {
        _commandsDir = commandsDir;
        _responsesDir = responsesDir;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (string file in Directory.GetFiles(_commandsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string content;
                try
                {
                    content = File.ReadAllText(file);
                    File.Delete(file);
                }
                catch (IOException)
                {
                    continue;
                }

                RawFiles.Enqueue(content);
                Match cmd = CommandPattern.Match(content);
                Match id = IdPattern.Match(content);
                if (!cmd.Success || cmd.Groups[1].Value.Length == 0) continue;

                string command = cmd.Groups[1].Value;
                ParsedCommands.Enqueue(command);
                var answer = Handler(command);
                if (answer is null || !id.Success) continue;

                string replyId = ForgeId ? "999" : id.Groups[1].Value;
                string json = $"{{\"id\":\"{replyId}\",\"response\":\"{answer.Value.Text.Replace("\"", "\\\"")}\",\"success\":{(answer.Value.Ok ? "true" : "false")}}}";
                string target = Path.Combine(_responsesDir, id.Groups[1].Value + ".json");
                await WriteAsync(target, json);
            }
            try { await Task.Delay(20, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    private async Task WriteAsync(string path, string json)
    {
        if (SlowWriteMs <= 0)
        {
            File.WriteAllText(path, json);
            return;
        }
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        int half = bytes.Length / 2;
        await fs.WriteAsync(bytes.AsMemory(0, half));
        await fs.FlushAsync();
        await Task.Delay(SlowWriteMs);
        await fs.WriteAsync(bytes.AsMemory(half));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(2000); } catch (AggregateException) { }
        _stop.Dispose();
    }
}

public sealed class FileBridgeTests : IDisposable
{
    private readonly string _root;
    private readonly string _commands;
    private readonly string _responses;

    public FileBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scumrcon-fb-" + Guid.NewGuid().ToString("N"));
        _commands = Path.Combine(_root, "rcon_commands");
        _responses = Path.Combine(_root, "rcon_responses");
        Directory.CreateDirectory(_commands);
        Directory.CreateDirectory(_responses);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private FileGameBridge Bridge(double timeoutSeconds = 5) =>
        new(_commands, _responses, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task Command_RoundTrips_ThroughTheScriptFormat()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses);
        using FileGameBridge bridge = Bridge();

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal("Executed: ListPlayers", result.Text);
    }

    [Fact]
    public async Task RequestFile_HasExactlyTheShapeTheScriptParses()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses);
        using FileGameBridge bridge = Bridge();

        await bridge.ExecuteAsync("ListFlags", CancellationToken.None);

        string raw = Assert.Single(lua.RawFiles);
        Assert.Matches("^\\{\"command\":\"ListFlags\",\"id\":\"\\d{10,20}\"\\}$", raw);
        Assert.Equal("ListFlags", Assert.Single(lua.ParsedCommands));
    }

    [Fact]
    public async Task PunctuationAndNonAscii_AreWrittenRaw_NotEscaped()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses);
        using FileGameBridge bridge = Bridge();
        const string command = "#Announce it's 100% <ok> & 1+1 สวัสดี";

        BridgeResult result = await bridge.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal(command, Assert.Single(lua.ParsedCommands));
        Assert.DoesNotContain("\\u", Assert.Single(lua.RawFiles));
    }

    [Theory]
    [InlineData("Announce \"hi\"")]
    [InlineData("Teleport C:\\temp")]
    [InlineData("ListPlayers\nSpawnItem Gold 1")]
    [InlineData("ListPlayers\t")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnsafeCommands_AreRefused_AndNothingIsWritten(string command)
    {
        using FileGameBridge bridge = Bridge();

        BridgeResult result = await bridge.ExecuteAsync(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(_commands));
    }

    [Fact]
    public async Task NobodyPicksUpTheCommand_TimesOut_AndTheFileIsRemoved()
    {
        using FileGameBridge bridge = Bridge(0.4);

        BridgeResult result = await bridge.ExecuteAsync("SpawnItem Gold 100", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not pick up", result.Text);
        Assert.Empty(Directory.GetFiles(_commands)); // must not run later when the game comes back
    }

    [Fact]
    public async Task CommandTakenButNeverAnswered_IsReportedDifferently()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses) { Handler = _ => null };
        using FileGameBridge bridge = Bridge(0.5);

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("took the command", result.Text);
    }

    [Fact]
    public async Task FailureAnswer_IsPassedOn_WithAHint()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses) { Handler = _ => (false, "Failed to execute") };
        using FileGameBridge bridge = Bridge();

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("Failed to execute", result.Text);
        Assert.Contains("player", result.Text);
    }

    [Fact]
    public async Task AnswerWithTheWrongId_IsRejected()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses) { ForgeId = true };
        using FileGameBridge bridge = Bridge();

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("Executed", result.Text);
    }

    [Fact]
    public async Task HalfWrittenAnswer_IsWaitedFor()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses) { SlowWriteMs = 200 };
        using FileGameBridge bridge = Bridge();

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal("Executed: ListPlayers", result.Text);
    }

    [Fact]
    public async Task OldAnswers_AreCleanedUp_ButOtherFilesAreNotTouched()
    {
        string old = Path.Combine(_responses, "123.json");
        string recent = Path.Combine(_responses, "456.json");
        string other = Path.Combine(_responses, "keep.txt");
        string notOurs = Path.Combine(_responses, "readme.json");
        foreach (string f in new[] { old, recent, other, notOurs }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(notOurs, DateTime.UtcNow.AddHours(-2));

        using var lua = new FakeLuaConsumer(_commands, _responses);
        using FileGameBridge bridge = Bridge();
        await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(notOurs));
    }

    [Fact]
    public async Task SimultaneousCalls_EachGetTheirOwnAnswer_AndNothingIsLeftBehind()
    {
        using var lua = new FakeLuaConsumer(_commands, _responses);
        using FileGameBridge bridge = Bridge();

        Task<BridgeResult>[] calls = Enumerable.Range(1, 8)
            .Select(i => bridge.ExecuteAsync("Cmd" + i, CancellationToken.None))
            .ToArray();
        BridgeResult[] results = await Task.WhenAll(calls);

        for (int i = 0; i < results.Length; i++)
            Assert.Equal("Executed: Cmd" + (i + 1), results[i].Text);
        Assert.Empty(Directory.GetFiles(_commands));
        Assert.Empty(Directory.GetFiles(_responses));
    }

    [Fact]
    public async Task MissingCommandFolder_GivesAFriendlyFailure()
    {
        using var bridge = new FileGameBridge(Path.Combine(_root, "nope"), _responses, TimeSpan.FromSeconds(1));
        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("command folder", result.Text);
    }
}

public class FileBridgeConfigTests
{
    private static ServerConfig FileConfig()
    {
        ServerConfig c = ConfigLoader.CreateFirstRun(out _);
        c.Bridge.Mode = "File";
        c.Bridge.Secret = string.Empty; // not needed in File mode
        c.Bridge.CommandsDirectory = @"C:\game\bridge\rcon_commands";
        c.Bridge.ResponsesDirectory = @"C:\game\bridge\rcon_responses";
        return c;
    }

    [Fact]
    public void FileMode_NeedsNoSecret_AndIsValid()
    {
        Assert.Empty(ConfigLoader.Validate(FileConfig()));
    }

    [Fact]
    public void FileMode_RejectsRelativeAndIdenticalFolders()
    {
        ServerConfig c = FileConfig();
        c.Bridge.CommandsDirectory = @"bridge\rcon_commands";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("commandsDirectory must be a full"));

        c = FileConfig();
        c.Bridge.ResponsesDirectory = c.Bridge.CommandsDirectory + @"\";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("different folders"));
    }

    [Fact]
    public void UnknownMode_IsRejected_AndModeIsCaseInsensitive()
    {
        ServerConfig c = FileConfig();
        c.Bridge.Mode = "Carrier pigeon";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("'Tcp' or 'File'"));

        c.Bridge.Mode = "fIlE";
        Assert.Empty(ConfigLoader.Validate(c));
    }

    [Fact]
    public void TcpMode_StillNeedsASecret()
    {
        ServerConfig c = FileConfig();
        c.Bridge.Mode = "Tcp";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("bridge.secret"));
    }

    [Fact]
    public void Factory_BuildsTheBridgeThatWasChosen()
    {
        ServerConfig c = FileConfig();
        using IDisposable file = (IDisposable)GameBridgeFactory.Create(c.Bridge);
        Assert.IsType<FileGameBridge>(file);

        c.Bridge.Mode = "Tcp";
        using IDisposable tcp = (IDisposable)GameBridgeFactory.Create(c.Bridge);
        Assert.IsType<TcpGameBridge>(tcp);
    }
}
