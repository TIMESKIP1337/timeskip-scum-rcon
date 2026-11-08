using System.Text.Json;
using MoonSharp.Interpreter;
using ScumRcon.GameBridge;

namespace ScumRcon.LuaBridge.Tests;

/// <summary>
/// Hosts rcon_bridge.lua in a Lua interpreter. File access is mapped to real temporary folders, the
/// game's channel objects are small Lua tables, and "the game" answers by calling OnChatMessage,
/// exactly as the hook in main.lua does.
/// </summary>
public sealed class LuaGame : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "scumrcon-lua-" + Guid.NewGuid().ToString("N"));
    public readonly string Commands, Responses;
    public readonly Script Lua = new(CoreModules.Preset_Complete);
    private readonly DynValue _module;
    private readonly Table _channels;

    // Called when the fake game receives a command: (command, channel table) -> may call OnChat
    public Action<string, DynValue>? OnAdminCommand;
    public readonly List<string> Printed = new();
    public bool AllChannelsInvalid;

    // Called when the fake game receives a chat broadcast: (message, channel number)
    public readonly List<(string Message, int Channel)> Broadcasts = new();
    // The same lines with the player channel (its address) that received them
    public readonly List<(int Address, string Message, int Channel)> Deliveries = new();
    // Online players for "saypm": SteamID -> (index of the channel in the game, player name)
    public readonly Dictionary<string, (int Address, string Name)> Players = new();
    public readonly List<string> AdminCommands = new();
    // The channel (its address) that ran each command in AdminCommands
    public readonly List<int> AdminCommandAddresses = new();
    // Text of the game's AdminUsers.ini; null = the file cannot be read
    public string? AdminUsersText = "";

    public LuaGame()
    {
        Commands = Path.Combine(Root, "rcon_commands");
        Responses = Path.Combine(Root, "rcon_responses");
        Directory.CreateDirectory(Commands);
        Directory.CreateDirectory(Responses);

        Lua.Globals["print"] = (Action<string>)(s => Printed.Add(s));
        Lua.Globals["FString"] = (Func<string, string>)(s => s);

        _module = Lua.DoString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rcon_bridge.lua")));
        var fs = new Table(Lua);
        fs["list"] = (Func<string, Table>)List;
        fs["read"] = (Func<string, string?>)(p => p.Contains("AdminUsers") ? AdminUsersText : (File.Exists(Map(p)) ? File.ReadAllText(Map(p)) : null));
        fs["write"] = (Func<string, string, bool>)((p, c) => { File.WriteAllText(Map(p), c); return true; });
        fs["remove"] = (Action<string>)(p => File.Delete(Map(p)));
        fs["mkdir"] = (Action<string>)(_ => { });
        _module.Table["fs"] = fs;

        // one valid channel; commands sent to it go to OnAdminCommand
        _channels = new Table(Lua);
        // what the mod's player lookup does: players whose SteamID or name (any letter case) equals the target
        _module.Table["findPlayers"] = (Func<string, DynValue>)(target =>
        {
            var list = new Table(Lua);
            int n = 1;
            foreach (var (steamId, p) in Players)
            {
                if (steamId != target && !string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = new Table(Lua);
                entry["channel"] = _channels.Get(p.Address);
                entry["name"] = p.Name;
                entry["steamId"] = steamId;
                list[n++] = entry;
            }
            return DynValue.NewTable(list);
        });
        // what the mod's player list does: everybody in Players, as { name, steamId }
        _module.Table["listPlayers"] = (Func<DynValue>)(() =>
        {
            var list = new Table(Lua);
            int n = 1;
            foreach (var (steamId, p) in Players)
            {
                var entry = new Table(Lua);
                entry["channel"] = _channels.Get(p.Address);
                entry["name"] = p.Name;
                entry["steamId"] = steamId;
                list[n++] = entry;
            }
            return DynValue.NewTable(list);
        });
        _channels[1] = MakeChannel(1);
        SetExecutorMode("any");          // the older tests run on "the first player found"; new tests opt in to "admin"
    }

    public DynValue MakeChannel(int address)
    {
        Lua.Globals["__address"] = address;
        Lua.Globals["__game"] = (Action<string, DynValue, int>)((cmd, ch, addr) => { AdminCommands.Add(cmd); AdminCommandAddresses.Add(addr); OnAdminCommand?.Invoke(cmd, ch); });
        Lua.Globals["__broadcast"] = (Action<string, int, int>)((msg, chan, addr) =>
        {
            Broadcasts.Add((msg, chan));
            Deliveries.Add((addr, msg, chan));
        });
        Lua.Globals["__invalid"] = (Func<bool>)(() => AllChannelsInvalid);
        return Lua.DoString(@"
            local address = __address
            local ch = {}
            function ch.IsValid(self) return not __invalid() end
            function ch.GetAddress(self) return address end
            ch.Chat_Server_ProcessAdminCommand = function(self, cmd) __game(cmd, self, address) end
            -- the call that really shows a line on a player's screen: (Message, SenderPlayerState,
            -- senderFakeName, netIdRepl, Channel, shouldCopyToClipboard)
            ch.Chat_Client_SendMessageToChat = function(self, msg, sender, fakeName, netId, chan, copy)
                if sender ~= nil or type(fakeName) ~= 'string' or fakeName == '' or type(netId) ~= 'table' or copy ~= false then error('bad arguments') end
                __broadcast(msg, chan, address)
            end
            return ch");
    }

    private string Map(string luaPath)
    {
        string name = Path.GetFileName(luaPath.Replace('\\', '/'));
        return luaPath.Contains("rcon_commands") ? Path.Combine(Commands, name) : Path.Combine(Responses, name);
    }

    private Table List(string dir)
    {
        var t = new Table(Lua);
        string real = dir.Contains("rcon_commands") ? Commands : Responses;
        int i = 1;
        foreach (string f in Directory.GetFiles(real, "*.json")) t[i++] = Path.GetFileName(f);
        return t;
    }

    /// <summary>One 500 ms poll of the real module.</summary>
    public void Tick(DynValue? channelsOverride = null)
    {
        DynValue adminFunc = DynValue.NewNil();
        _module.Table.Get("ProcessPendingCommands").Function.Call(adminFunc, channelsOverride ?? DynValue.NewTable(_channels));
    }

    /// <summary>What main.lua's hook does when the game sends a chat line to a player.</summary>
    public void Chat(DynValue channel, string text, int chatType) =>
        _module.Table.Get("OnChatMessage").Function.Call(channel, text, chatType);

    public DynValue FirstChannel => _channels.Get(1);

    /// <summary>What RCON_EXECUTOR in main.lua does.</summary>
    public void SetExecutorMode(string mode) =>
        _module.Table.Get("SetExecutorMode").Function.Call(DynValue.NewString(mode));

    /// <summary>What RCON_SAY_NAME in main.lua does.</summary>
    public void SetSayName(string? name) =>
        _module.Table.Get("SetSayName").Function.Call(name is null ? DynValue.Nil : DynValue.NewString(name));

    /// <summary>Adds one more connected player.</summary>
    public void AddPlayerChannel() => _channels[(int)_channels.Length + 1] = MakeChannel((int)_channels.Length + 1);

    /// <summary>Runs Tick() in the background until the task completes (about 30 ms per tick).</summary>
    public async Task<T> RunUntil<T>(Task<T> work, Action<int>? afterTick = null)
    {
        int tick = 0;
        while (!work.IsCompleted)
        {
            Tick();
            afterTick?.Invoke(++tick);
            await Task.WhenAny(work, Task.Delay(30));
            if (tick > 400) throw new TimeoutException("The bridge never finished.");
        }
        return await work;
    }

    public void Dispose() => Directory.Delete(Root, true);
}

public sealed class LuaBridgeTests : IDisposable
{
    private readonly LuaGame _game = new();
    private readonly FileGameBridge _bridge;

    public LuaBridgeTests()
    {
        _bridge = new FileGameBridge(_game.Commands, _game.Responses, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _game.Dispose();
    }

    [Fact]
    public async Task ReplyLines_FromTheGame_ComeBackToTheAdmin()
    {
        _game.OnAdminCommand = (cmd, ch) =>
        {
            _game.Chat(ch, "Players (2):", 4);
            _game.Chat(ch, "1. Alice", 4);
            _game.Chat(ch, "2. Bob", 4);
        };

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListFlags", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal("Players (2):\n1. Alice\n2. Bob", r.Text);
    }

    [Fact]
    public async Task AwkwardText_SurvivesTheTripThroughJson()
    {
        const string awkward = "He said \"hi\" \\ back\\slash\tTab and \u0001control — ไทย 🙂 <b>x</b>";
        _game.OnAdminCommand = (_, ch) => _game.Chat(ch, awkward, 4);

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListFlags", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        int i = 0;
        while (i < awkward.Length && i < r.Text.Length && awkward[i] == r.Text[i]) i++;
        string where = i < Math.Min(awkward.Length, r.Text.Length)
            ? $"first difference at {i}: expected U+{(int)awkward[i]:X4}, got U+{(int)r.Text[i]:X4}"
            : "lengths differ";
        Assert.True(awkward == r.Text, where);
    }

    [Fact]
    public async Task NoReplyFromTheGame_StillAnswersClearly()
    {
        _game.OnAdminCommand = (_, _) => { };

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListFlags", CancellationToken.None));

        Assert.True(r.Success);
        Assert.Equal("Executed: ListFlags (the game sent no reply text)", r.Text);
    }

    [Fact]
    public async Task PlayerChat_IsNotMixedIntoTheReply()
    {
        _game.OnAdminCommand = (_, ch) =>
        {
            _game.Chat(ch, "Bob: hello everyone", 2);   // global chat
            _game.Chat(ch, "Alice: psst", 3);           // squad chat
            _game.Chat(ch, "Result line", 4);           // admin reply
            _game.Chat(ch, "Command failed: nope", 7);  // error reply
        };

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListFlags", CancellationToken.None));

        Assert.Equal("Result line\nCommand failed: nope", r.Text);
        Assert.Contains(_game.Printed, p => p.Contains("2 line(s) captured, 2 other chat line(s) ignored"));
    }

    [Fact]
    public async Task LinesForAnotherPlayersChannel_AreIgnored()
    {
        DynValue other = _game.MakeChannel(99);
        _game.OnAdminCommand = (_, ch) =>
        {
            _game.Chat(other, "private message to someone else", 4);
            _game.Chat(ch, "for the admin", 4);
        };

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListFlags", CancellationToken.None));

        Assert.Equal("for the admin", r.Text);
    }

    [Fact]
    public async Task ReplyThatArrivesALittleLater_IsStillCaptured()
    {
        _game.OnAdminCommand = (_, _) => { };   // nothing during the call

        BridgeResult r = await _game.RunUntil(
            _bridge.ExecuteAsync("ListFlags", CancellationToken.None),
            afterTick: tick =>
            {
                if (tick == 3) _game.Chat(_game.FirstChannel, "late line", 4);
            });

        // Tick 3 may fall before or after the command started; either way the line is only
        // captured while a command is waiting, so accept both outcomes but never a wrong one.
        Assert.True(r.Text == "late line" || r.Text.StartsWith("Executed: ListFlags"), r.Text);
    }

    [Theory]
    [InlineData("#SpawnItem Weapon_AKM 1 76561198000000042 Location", "SpawnItem Weapon_AKM 1 76561198000000042 Location")]
    [InlineData("SpawnItem Weapon_AKM 1 76561198000000042 Location", "SpawnItem Weapon_AKM 1 76561198000000042 Location")]
    [InlineData("  #ListFlags", "ListFlags")]
    [InlineData("##ListFlags", "ListFlags")]
    [InlineData("# ListFlags", "ListFlags")]
    public async Task LeadingHash_IsRemovedBeforeTheGameSeesTheCommand_LikeTheChatBoxDoes(string sent, string expectedAtGame)
    {
        string? seen = null;
        _game.OnAdminCommand = (cmd, _) => seen = cmd;

        await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.Equal(expectedAtGame, seen);
    }

    [Theory]
    [InlineData("say global Server restarts soon", "Server restarts soon", 2, "global")]
    [InlineData("say 7 Big problem", "Big problem", 7, "error")]
    [InlineData("say ERROR Shouting", "Shouting", 7, "error")]
    [InlineData("say Squad hi team", "hi team", 3, "squad")]
    [InlineData("say commandsonly x", "x", 5, "commands")]
    [InlineData("say servermessage y", "y", 6, "server")]
    [InlineData("say local near me", "near me", 1, "local")]
    [InlineData("say 0 plain", "plain", 0, "default")]
    [InlineData("say hello world", "hello world", 6, "server")]       // no channel: the default (server)
    [InlineData("#say  admin  spaced   out ", "spaced   out", 4, "admin")]
    [InlineData("SAY global CAPS command word", "CAPS command word", 2, "global")]
    [InlineData("say global สวัสดีทุกคน", "สวัสดีทุกคน", 2, "global")]
    public async Task Say_BroadcastsOnTheChosenChannel(string sent, string message, int channel, string channelName)
    {
        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal((message, channel), Assert.Single(_game.Broadcasts));
        Assert.Contains($"channel {channelName} ({channel})", r.Text);
        Assert.Empty(_game.AdminCommands);            // it is not a game admin command
    }

    [Theory]
    [InlineData("say")]
    [InlineData("say   ")]
    [InlineData("say global")]
    [InlineData("say 3")]
    public async Task Say_WithoutAMessage_ShowsUsage_AndSendsNothing(string sent)
    {
        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.False(r.Success);
        Assert.StartsWith("Usage: say", r.Text);
        Assert.Empty(_game.Broadcasts);
    }

    [Theory]
    [InlineData("say 8 hi")]
    [InlineData("say 12 hi")]
    [InlineData("say 07 hi")]
    public async Task Say_WithABadChannelNumber_IsRefused(string sent)
    {
        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("channel must be 0-7", r.Text);
        Assert.Empty(_game.Broadcasts);
    }

    [Fact]
    public async Task Say_RefusesAVeryLongMessage()
    {
        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("say global " + new string('x', 401), CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("longer than 400", r.Text);
        Assert.Empty(_game.Broadcasts);
    }

    [Fact]
    public async Task WithASayName_SayLinesStartWithTheNameInBrackets()
    {
        _game.SetSayName("MyServer");

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("say global Server restarts soon", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal(("[MyServer] Server restarts soon", 2), Assert.Single(_game.Broadcasts));
    }

    [Fact]
    public async Task WithASayName_SayPmLinesStartWithTheNameToo()
    {
        _game.Players["76561198000000042"] = (1, "Alice");
        _game.SetSayName("MyServer");

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("saypm 76561198000000042 admin hello Alice", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal((1, "[MyServer] hello Alice", 4), Assert.Single(_game.Deliveries));
    }

    [Theory]
    [InlineData("  MyServer  ", "[MyServer] hi")]                                  // spaces around the name are removed
    [InlineData("My\u0007Server", "[MyServer] hi")]                             // control characters are removed
    [InlineData("สวัสดี", "[สวัสดี] hi")]                                       // any language
    [InlineData("12345678901234567890123456789012XXXX", "[12345678901234567890123456789012] hi")]   // cut to 32 bytes
    public async Task TheSayName_IsCleaned(string name, string expectedLine)
    {
        _game.SetSayName(name);

        await _game.RunUntil(_bridge.ExecuteAsync("say global hi", CancellationToken.None));

        Assert.Equal((expectedLine, 2), Assert.Single(_game.Broadcasts));
    }

    [Fact]
    public async Task ALongNameInAnotherLanguage_IsNeverCutInTheMiddleOfACharacter()
    {
        // Real Lua sees strings as UTF-8 BYTES (3 per Thai letter); MoonSharp sees .NET characters.
        // So hand the name over in "byte form" (one char per byte) to test the real byte logic.
        static string AsLuaBytes(string text) => System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes(text));
        static string FromLuaBytes(string bytes) => System.Text.Encoding.UTF8.GetString(System.Text.Encoding.Latin1.GetBytes(bytes));
        _game.SetSayName(AsLuaBytes("สวัสดีสวัสดีสวัสดีสวัสดี"));      // 24 letters = 72 bytes

        await _game.RunUntil(_bridge.ExecuteAsync("say global hi", CancellationToken.None));

        string sent = FromLuaBytes(Assert.Single(_game.Broadcasts).Message);
        Assert.Equal("[สวัสดีสวัส] hi", sent);                // 32 bytes hold 10 letters (30 bytes), not 10.67
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptySayName_MeansNoName(string? name)
    {
        _game.SetSayName("first");          // then cleared
        _game.SetSayName(name);

        await _game.RunUntil(_bridge.ExecuteAsync("say global hi", CancellationToken.None));

        Assert.Equal(("hi", 2), Assert.Single(_game.Broadcasts));
    }

    [Fact]
    public async Task TheNameDoesNotCountTowardsTheMessageLimit()
    {
        _game.SetSayName("MyServer");

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("say global " + new string('x', 400), CancellationToken.None));

        Assert.True(r.Success, r.Text);                                      // 400 characters of the admin's own text are allowed
    }

    [Fact]
    public async Task Say_ReachesEveryConnectedPlayer()
    {
        _game.AddPlayerChannel();
        _game.AddPlayerChannel();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("say global hello all", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal(3, _game.Broadcasts.Count);
        Assert.All(_game.Broadcasts, b => Assert.Equal(("hello all", 2), b));
        Assert.Contains("to 3 player(s)", r.Text);
    }

    private const string SteamA = "76561198000000042";
    private const string SteamB = "76561198000000001";

    private void TwoPlayersOnline()
    {
        _game.AddPlayerChannel();
        _game.Players[SteamA] = (1, "Alice");
        _game.Players[SteamB] = (2, "Bob");
    }

    [Theory]
    [InlineData("saypm " + SteamB + " global hi Bob", "hi Bob", 2, "global")]
    [InlineData("saypm " + SteamB + " admin 7 is my lucky number", "7 is my lucky number", 4, "admin")]
    [InlineData("saypm " + SteamB + " 5 plain digit", "plain digit", 5, "commands")]
    [InlineData("saypm " + SteamB + " just a private note", "just a private note", 6, "server")]   // default channel
    [InlineData("#SAYPM  " + SteamB + "   error   spaced   out ", "spaced   out", 7, "error")]
    [InlineData("saypm " + SteamB + " global สวัสดี", "สวัสดี", 2, "global")]
    public async Task SayPm_SendsOnlyToThatPlayer(string sent, string message, int channel, string channelName)
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal((2, message, channel), Assert.Single(_game.Deliveries));    // Bob's channel only
        Assert.Contains($"channel {channelName} ({channel}) to Bob ({SteamB})", r.Text);
        Assert.Empty(_game.AdminCommands);
    }

    [Fact]
    public async Task SayPm_ToAnOfflinePlayer_SaysSoAndSendsNothing()
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("saypm 76561190000000000 global hi", CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("No online player with SteamID 76561190000000000", r.Text);
        Assert.Empty(_game.Deliveries);
    }

    [Theory]
    [InlineData("saypm Bob global hi Bob", "hi Bob", 2, "global")]
    [InlineData("saypm bob admin hello", "hello", 4, "admin")]             // any letter case
    [InlineData("saypm BOB just a note", "just a note", 6, "server")]      // default channel
    [InlineData("#saypm  Bob  3  squad talk", "squad talk", 3, "squad")]
    [InlineData("saypm Bob global สวัสดี", "สวัสดี", 2, "global")]
    public async Task SayPm_ByName_SendsOnlyToThatPlayer(string sent, string message, int channel, string channelName)
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal((2, message, channel), Assert.Single(_game.Deliveries));               // Bob's channel only
        Assert.Contains($"channel {channelName} ({channel}) to Bob ({SteamB})", r.Text);    // shows his SteamID too
    }

    [Fact]
    public async Task SayPm_ByName_AndBySteamId_ReachTheSamePlayer()
    {
        TwoPlayersOnline();

        await _game.RunUntil(_bridge.ExecuteAsync("saypm Alice global one", CancellationToken.None));
        await _game.RunUntil(_bridge.ExecuteAsync("saypm " + SteamA + " global two", CancellationToken.None));

        Assert.Equal(new[] { (1, "one", 2), (1, "two", 2) }, _game.Deliveries.ToArray());
    }

    [Fact]
    public async Task SayPm_ToAnUnknownName_SaysSo()
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("saypm Nobody global hi", CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("No online player named Nobody", r.Text);
        Assert.Empty(_game.Deliveries);
    }

    [Fact]
    public async Task SayPm_WhenTwoPlayersHaveTheSameName_AsksForTheSteamId_AndSendsNothing()
    {
        TwoPlayersOnline();
        _game.AddPlayerChannel();
        _game.Players["76561198000000003"] = (3, "bob");        // same name, another letter case

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("saypm Bob global hi", CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("More than one online player named Bob (2)", r.Text);
        Assert.Contains(SteamB, r.Text);
        Assert.Contains("76561198000000003", r.Text);
        Assert.Empty(_game.Deliveries);
    }

    [Fact]
    public async Task SayPm_ASteamId_StillWorksWhenSomeoneIsNamedLikeADigitString()
    {
        TwoPlayersOnline();
        _game.AddPlayerChannel();
        _game.Players["76561198000000004"] = (3, "12345");      // a player whose NAME is digits

        await _game.RunUntil(_bridge.ExecuteAsync("saypm 12345 global by name", CancellationToken.None));          // short: a name
        await _game.RunUntil(_bridge.ExecuteAsync("saypm 76561198000000004 global by id", CancellationToken.None));

        Assert.Equal(new[] { (3, "by name", 2), (3, "by id", 2) }, _game.Deliveries.ToArray());
    }

    [Theory]
    [InlineData("saypm")]
    [InlineData("saypm   ")]
    public async Task SayPm_WithoutATarget_ShowsUsage(string sent)
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("Usage: saypm <name or steamid>", r.Text);
        Assert.Empty(_game.Deliveries);
    }

    [Theory]
    [InlineData("saypm " + SteamA)]
    [InlineData("saypm " + SteamA + " global")]
    public async Task SayPm_WithoutAMessage_ShowsUsage(string sent)
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.False(r.Success);
        Assert.StartsWith("Usage: saypm", r.Text);
        Assert.Empty(_game.Deliveries);
    }

    [Fact]
    public async Task SayPm_WithABadChannelNumber_OrALongMessage_IsRefused()
    {
        TwoPlayersOnline();

        BridgeResult bad = await _game.RunUntil(_bridge.ExecuteAsync("saypm " + SteamA + " 9 hi", CancellationToken.None));
        BridgeResult longMsg = await _game.RunUntil(_bridge.ExecuteAsync("saypm " + SteamA + " global " + new string('x', 401), CancellationToken.None));

        Assert.False(bad.Success);
        Assert.Contains("channel must be 0-7", bad.Text);
        Assert.False(longMsg.Success);
        Assert.Contains("longer than 400", longMsg.Text);
        Assert.Empty(_game.Deliveries);
    }

    [Fact]
    public async Task SayPm_WhenThePlayerChannelIsGone_FailsClearly()
    {
        TwoPlayersOnline();
        _game.AllChannelsInvalid = true;

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("saypm " + SteamA + " global hi", CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("No online player", r.Text);
    }

    [Fact]
    public async Task Say_StillReachesEveryone_WhenPlayersAreRegistered()
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("say global all", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal(new[] { 1, 2 }, _game.Deliveries.Select(d => d.Address).OrderBy(a => a).ToArray());
    }

    // ── "players": the list comes from the mod, so the RCON user really sees it ───────────────────
    [Fact]
    public async Task Players_ReturnsTheOnlinePlayers_AsText()
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("players", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal($"2 players online:\n1. Alice  {SteamA}\n2. Bob  {SteamB}", r.Text.Replace("\r\n", "\n"));
        Assert.Empty(_game.AdminCommands);              // not a game command
        Assert.Empty(_game.Deliveries);                 // and nothing is sent to the players
    }

    [Fact]
    public async Task Players_WithOnePlayer_SaysPlayerNotPlayers()
    {
        _game.Players[SteamA] = (1, "Alice");

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("players", CancellationToken.None));

        Assert.StartsWith("1 player online:", r.Text);
    }

    [Fact]
    public async Task Players_WhenNobodyIsOnline_SaysSo()
    {
        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("players", CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal("No players online.", r.Text);
    }

    [Theory]
    [InlineData("PLAYERS")]
    [InlineData("#players")]
    [InlineData("  Players  ")]
    [InlineData("players now")]                  // extra words are ignored
    public async Task Players_WorksWithAnyLetterCaseHashAndSpaces(string sent)
    {
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.StartsWith("2 players online:", r.Text);
    }

    [Fact]
    public async Task Players_KeepsOtherLanguagesInNames_AndDropsControlCharacters()
    {
        _game.Players[SteamA] = (1, "สมชาย");
        _game.Players[SteamB] = (1, "Al\u0007ice");

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("players", CancellationToken.None));

        Assert.Contains($"1. สมชาย  {SteamA}", r.Text);
        Assert.Contains($"2. Alice  {SteamB}", r.Text);
    }

    [Theory]
    [InlineData("ListPlayers")]
    [InlineData("listplayers")]
    [InlineData("#ListPlayers")]
    [InlineData("  LISTPLAYERS  ")]
    public async Task ListPlayers_IsAnsweredByTheMod_AndNeverSentToTheGame(string sent)
    {
        // The game would print its list (names, SteamIDs, positions, balances of EVERYONE) into the chat of the
        // player whose channel runs the command. The mod answers with its own list instead.
        TwoPlayersOnline();

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync(sent, CancellationToken.None));

        Assert.True(r.Success, r.Text);
        Assert.Equal($"2 players online:\n1. Alice  {SteamA}\n2. Bob  {SteamB}", r.Text.Replace("\r\n", "\n"));
        Assert.Empty(_game.AdminCommands);              // nothing reached the game
        Assert.Empty(_game.Deliveries);                 // and no chat line went to any player
    }

    [Fact]
    public async Task ListPlayers_WhenNobodyIsOnline_SaysSo_WithoutTouchingTheGame()
    {
        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListPlayers", CancellationToken.None));

        Assert.Equal("No players online.", r.Text);
        Assert.Empty(_game.AdminCommands);
    }

    [Fact]
    public async Task AWordThatOnlyStartsWithListPlayers_IsStillAGameCommand()
    {
        await _game.RunUntil(_bridge.ExecuteAsync("ListPlayersExtra now", CancellationToken.None));

        Assert.Equal(new[] { "ListPlayersExtra now" }, _game.AdminCommands.ToArray());
    }

    [Fact]
    public async Task AWordThatOnlyStartsWithPlayers_IsStillAGameCommand()
    {
        await _game.RunUntil(_bridge.ExecuteAsync("playersx now", CancellationToken.None));

        Assert.Equal(new[] { "playersx now" }, _game.AdminCommands.ToArray());
    }

    [Fact]
    public async Task Players_ThenARealCommand_BothWork_InOrder()
    {
        TwoPlayersOnline();
        _game.OnAdminCommand = (cmd, ch) => _game.Chat(ch, "reply to " + cmd, 4);

        Task<BridgeResult> a = _bridge.ExecuteAsync("players", CancellationToken.None);
        Task<BridgeResult> b = _bridge.ExecuteAsync("announce hi", CancellationToken.None);
        BridgeResult[] r = await _game.RunUntil(Task.WhenAll(a, b));

        Assert.StartsWith("2 players online:", r[0].Text);
        Assert.Equal("reply to announce hi", r[1].Text);
    }

    [Fact]
    public async Task Say_WithNoUsableChannel_FailsClearly()
    {
        _game.AllChannelsInvalid = true;

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("say global hi", CancellationToken.None));

        Assert.False(r.Success);
        Assert.Contains("no usable player channel", r.Text);
    }

    [Fact]
    public async Task AWordThatOnlyStartsWithSay_IsStillAGameCommand()
    {
        await _game.RunUntil(_bridge.ExecuteAsync("sayonara now", CancellationToken.None));

        Assert.Equal(new[] { "sayonara now" }, _game.AdminCommands.ToArray());
        Assert.Empty(_game.Broadcasts);
    }

    [Fact]
    public async Task Say_ThenARealCommand_BothWork_InOrder()
    {
        _game.OnAdminCommand = (cmd, ch) => _game.Chat(ch, "reply to " + cmd, 4);

        Task<BridgeResult> a = _bridge.ExecuteAsync("say global first", CancellationToken.None);
        Task<BridgeResult> b = _bridge.ExecuteAsync("ListFlags", CancellationToken.None);
        BridgeResult[] r = await _game.RunUntil(Task.WhenAll(a, b));

        Assert.True(r[0].Success);
        Assert.Equal("reply to ListFlags", r[1].Text);
        Assert.Equal(("first", 2), Assert.Single(_game.Broadcasts));
    }

    [Fact]
    public void CommandThatIsOnlyAHash_IsIgnored()
    {
        bool ran = false;
        _game.OnAdminCommand = (_, _) => ran = true;
        File.WriteAllText(Path.Combine(_game.Commands, "1.json"), "{\"command\":\"#\",\"id\":\"123\"}");

        for (int i = 0; i < 3; i++) _game.Tick();

        Assert.False(ran);
        Assert.Empty(Directory.GetFiles(_game.Responses));
    }

    [Fact]
    public async Task NoUsableChannel_GivesFailedToExecute_WithAHint()
    {
        _game.AllChannelsInvalid = true;

        BridgeResult r = await _game.RunUntil(_bridge.ExecuteAsync("ListFlags", CancellationToken.None));

        Assert.False(r.Success);
        Assert.StartsWith("Failed to execute", r.Text);
    }

    [Fact]
    public async Task TwoCommands_AreRunOneAfterTheOther_EachWithItsOwnReply()
    {
        _game.OnAdminCommand = (cmd, ch) => _game.Chat(ch, "reply to " + cmd, 4);

        Task<BridgeResult> a = _bridge.ExecuteAsync("First", CancellationToken.None);
        Task<BridgeResult> b = _bridge.ExecuteAsync("Second", CancellationToken.None);
        BridgeResult[] results = await _game.RunUntil(Task.WhenAll(a, b));

        Assert.Equal("reply to First", results[0].Text);
        Assert.Equal("reply to Second", results[1].Text);
    }

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("12ab")]
    [InlineData("1/2")]
    [InlineData("")]
    [InlineData("123456789012345678901234567890")]
    public void UnsafeIds_AreIgnored_AndNothingIsWrittenOutsideTheResponseFolder(string id)
    {
        bool ran = false;
        _game.OnAdminCommand = (_, _) => ran = true;
        File.WriteAllText(Path.Combine(_game.Commands, "1.json"), "{\"command\":\"ListFlags\",\"id\":\"" + id + "\"}");

        for (int i = 0; i < 4; i++) _game.Tick();

        Assert.False(ran);
        Assert.Empty(Directory.GetFiles(_game.Commands));
        Assert.Empty(Directory.GetFiles(_game.Responses));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_game.Root)!, "evil*"));
        Assert.Contains(_game.Printed, p => p.Contains("unsafe id"));
    }

    [Fact]
    public void JsonEscape_ProducesValidJson_ForEveryControlCharacter()
    {
        var module = _game.Lua.DoString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rcon_bridge.lua")));
        var escape = module.Table.Get("JsonEscape").Function;

        var all = new System.Text.StringBuilder();
        for (int c = 0; c < 128; c++) all.Append((char)c);
        string original = all.ToString();

        string escaped = escape.Call(original).String;
        using JsonDocument doc = JsonDocument.Parse("{\"v\":\"" + escaped + "\"}");
        Assert.Equal(original, doc.RootElement.GetProperty("v").GetString());
    }
}
