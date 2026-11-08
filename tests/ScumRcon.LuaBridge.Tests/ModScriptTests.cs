using MoonSharp.Interpreter;

namespace ScumRcon.LuaBridge.Tests;

/// <summary>
/// Loads the real mod entry point (mods/ScumRcon/Scripts/main.lua) and its player lookup with a fake
/// UE4SS environment, to catch wiring mistakes without a game.
/// </summary>
public sealed class ModScriptTests
{
    private readonly Script _lua = new(CoreModules.Preset_Complete);
    private readonly Dictionary<string, DynValue> _modules = new();
    private readonly List<(string Name, DynValue Function)> _hooks = new();
    private readonly List<(int Milliseconds, DynValue Function)> _loops = new();
    private readonly List<string> _printed = new();

    public ModScriptTests()
    {
        _lua.Globals["print"] = (Action<string>)(s => _printed.Add(s));
        _lua.Globals["FString"] = (Func<string, string>)(s => s);
        _lua.Globals["StaticFindObject"] = (Func<string, DynValue>)(_ => DynValue.NewTable(_lua));
        _lua.Globals["RegisterHook"] = (Action<string, DynValue>)((name, fn) => _hooks.Add((name, fn)));
        _lua.Globals["LoopAsync"] = (Action<int, DynValue>)((ms, fn) => _loops.Add((ms, fn)));
        _lua.Globals["require"] = (Func<string, DynValue>)Require;

        _lua.DoString(@"
            __channels = {}
            FindAllOf = function(name) if name == 'PlayerRpcChannel' then return __channels end return nil end

            function MakeChannel(steamId, name, full)
                local pc = {}
                pc.PlayerState = { PlayerNamePrivate = { ToString = function() return name end } }
                function pc:GetUserId() return { ToString = function() return steamId end } end
                local ch = {}
                function ch:IsValid() return true end
                function ch:GetFullName() return full or 'PlayerRpcChannel /Game/Map.PlayerRpcChannel_1' end
                function ch:GetOwner() return pc end
                return ch
            end");
    }

    private DynValue Require(string name)
    {
        if (_modules.TryGetValue(name, out DynValue? loaded)) return loaded;
        string file = Path.Combine(AppContext.BaseDirectory, "mod", name.Replace('.', Path.DirectorySeparatorChar) + ".lua");
        DynValue module = _lua.DoString(File.ReadAllText(file));
        _modules[name] = module;
        return module;
    }

    private void RunMain() => _lua.DoString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mod", "main.lua")));

    [Fact]
    public void Main_RegistersTheReplyHook_AndTheHalfSecondLoop()
    {
        RunMain();

        Assert.Equal("/Script/SCUM.PlayerRpcChannel:Chat_Client_SendMessageToChat", Assert.Single(_hooks).Name);
        Assert.Equal(500, Assert.Single(_loops).Milliseconds);
        Assert.Contains(_printed, line => line.Contains("[ScumRcon] RCON bridge active"));
    }

    [Fact]
    public void RconSayName_SetInTheMod_BecomesTheNameInFrontOfSayLines()
    {
        _lua.Globals["RCON_SAY_NAME"] = "MyServer";

        RunMain();

        Assert.Equal("MyServer", Require("modules.rcon_bridge").Table.Get("sayName").String);
    }

    [Fact]
    public void WithoutRconSayName_TheDefaultNameIsTimeskipCon()
    {
        RunMain();

        Assert.Equal("TIMESKIPCON", Require("modules.rcon_bridge").Table.Get("sayName").String);
    }

    [Fact]
    public void AnEmptyRconSayName_MeansLinesWithoutAName()
    {
        _lua.Globals["RCON_SAY_NAME"] = "";

        RunMain();

        Assert.True(Require("modules.rcon_bridge").Table.Get("sayName").IsNil());
    }

    [Fact]
    public void TheExecutor_IsAdminOnlyByDefault()
    {
        RunMain();

        Assert.Equal("admin", Require("modules.rcon_bridge").Table.Get("executorMode").String);
    }

    [Theory]
    [InlineData("any", "any")]
    [InlineData("admin", "admin")]
    [InlineData("whatever", "admin")]
    public void RconExecutor_SetInTheMod_ChoosesTheMode(string setting, string expected)
    {
        _lua.Globals["RCON_EXECUTOR"] = setting;

        RunMain();

        Assert.Equal(expected, Require("modules.rcon_bridge").Table.Get("executorMode").String);
    }

    [Fact]
    public void TheAdminListPath_IsTheGamesOwnFile_RelativeToTheWin64Folder()
    {
        RunMain();

        string path = Require("modules.rcon_bridge").Table.Get("adminUsersFile").String;
        Assert.Equal(@"..\..\Saved\Config\WindowsServer\AdminUsers.ini", path);
    }

    [Fact]
    public void PlayerLookup_ListOnline_GivesEachPlayersChannel_SoTheBridgeCanPickAnAdmin()
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Alice'),
            MakeChannel('76561198000000002', 'Bob') }");
        DynValue lookup = Require("modules.player_lookup");

        Table list = _lua.Call(lookup.Table.Get("ListOnline")).Table;

        Assert.Equal(_lua.DoString("return __channels[1]").Table, list.Get(1).Table.Get("channel").Table);   // Alice
        Assert.Equal(_lua.DoString("return __channels[2]").Table, list.Get(2).Table.Get("channel").Table);   // Bob
    }

    [Fact]
    public void Debug_IsOffByDefault_OnlyTheReplyHookIsRegistered()
    {
        RunMain();

        Assert.Single(_hooks);
        Assert.DoesNotContain(_printed, line => line.Contains("[debug]"));
    }

    [Fact]
    public void Debug_WhenSwitchedOn_HooksTheCandidateFunctions_AndLogsWithoutErrors()
    {
        _lua.Globals["RCON_DEBUG"] = true;

        RunMain();

        string[] expected =
        {
            "/Script/SCUM.PlayerRpcChannel:Chat_Server_ProcessAdminCommand",
            "/Script/SCUM.PlayerRpcChannel:Chat_Client_ProcessAdminCommand",
            "/Script/SCUM.PlayerRpcChannel:Chat_Client_SendMessageToChat",
            "/Script/SCUM.PlayerRpcChannel:Chat_Client_SendSquadMessageToChat",
            "/Script/SCUM.MiscStatics:SendChatLineToPlayer",
            "/Script/SCUM.MiscStatics:BroadcastChatLine",
        };
        foreach (string path in expected) Assert.Contains(_hooks, h => h.Name == path);
        Assert.Contains(_printed, line => line.Contains("debug mode is ON"));

        // the debug callbacks must never raise errors into the game, whatever the game passes in
        DynValue wrapper = _lua.DoString("return { get = function() return 'x' end }");
        foreach (var (_, function) in _hooks.Where(h => h.Name.Contains("Chat") || h.Name.Contains("MiscStatics")))
            function.Function.Call(wrapper, wrapper, wrapper, wrapper, wrapper, wrapper, wrapper);
        Assert.Contains(_printed, line => line.Contains("[debug] Chat_Client_SendMessageToChat"));
    }

    [Fact]
    public void Debug_AHookThatCannotBeRegistered_IsReported_NotFatal()
    {
        _lua.Globals["RCON_DEBUG"] = true;
        _lua.Globals["RegisterHook"] = (Action<string, DynValue>)((name, _) =>
        {
            if (name.Contains("MiscStatics")) throw new ScriptRuntimeException("no such function");
            _hooks.Add((name, DynValue.Nil));
        });

        RunMain();

        Assert.Contains(_printed, line => line.Contains("MiscStatics:SendChatLineToPlayer FAILED"));
        Assert.Contains(_printed, line => line.Contains("RCON bridge active"));    // the mod still started
    }

    [Fact]
    public void TheLoop_RunsWithoutErrors_AndKeepsLooping()
    {
        RunMain();
        _lua.DoString("__channels = { MakeChannel('76561198000000001', 'Alice') }");

        DynValue result = _loops[0].Function.Function.Call();

        Assert.False(result.CastToBool());       // false = keep looping
    }

    [Fact]
    public void TheReplyHook_DoesNothing_WhenNoCommandIsWaiting()
    {
        RunMain();

        // the hook receives UE4SS parameter wrappers with :get()
        DynValue wrapper = _lua.DoString("return { get = function() return 'x' end }");
        DynValue hook = _hooks[0].Function;

        hook.Function.Call(wrapper, wrapper, wrapper, wrapper, wrapper, wrapper, wrapper);

        Assert.DoesNotContain(_printed, line => line.Contains("Could not"));
    }

    [Fact]
    public void PlayerLookup_FindsAPlayerBySteamId_ReturnsTheChannelAndName()
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Alice'),
            MakeChannel('76561198000000002', 'Bob') }");
        DynValue lookup = Require("modules.player_lookup");

        DynValue found = _lua.Call(lookup.Table.Get("FindOnlineBySteamId"), DynValue.NewString("76561198000000002"));

        Assert.Equal(2, found.Tuple.Length);
        Assert.Equal("Bob", found.Tuple[1].String);
        Assert.Equal(_lua.DoString("return __channels[2]").Table, found.Tuple[0].Table);
    }

    [Fact]
    public void PlayerLookup_ReturnsNothing_ForAnUnknownSteamId_OrAnEmptyServer()
    {
        _lua.DoString("__channels = { MakeChannel('76561198000000001', 'Alice') }");
        DynValue lookup = Require("modules.player_lookup");
        DynValue find = lookup.Table.Get("FindOnlineBySteamId");

        Assert.True(_lua.Call(find, DynValue.NewString("76561190000000000")).IsNil());
        Assert.True(_lua.Call(find, DynValue.NewString("")).IsNil());

        _lua.DoString("__channels = {}");
        Assert.True(_lua.Call(find, DynValue.NewString("76561198000000001")).IsNil());
    }

    [Fact]
    public void PlayerLookup_IgnoresTheClassDefaultObject()
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Default', 'PlayerRpcChannel /Script/SCUM.Default__PlayerRpcChannel') }");
        DynValue lookup = Require("modules.player_lookup");

        Assert.True(_lua.Call(lookup.Table.Get("FindOnlineBySteamId"), DynValue.NewString("76561198000000001")).IsNil());
    }

    [Theory]
    [InlineData("76561198000000002")]      // by SteamID
    [InlineData("Bob")]                    // by name
    [InlineData("bob")]                    // any letter case
    [InlineData("BOB")]
    public void PlayerLookup_FindOnline_FindsTheSamePlayer_ByNameOrBySteamId(string target)
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Alice'),
            MakeChannel('76561198000000002', 'Bob') }");
        DynValue lookup = Require("modules.player_lookup");

        DynValue found = _lua.Call(lookup.Table.Get("FindOnline"), DynValue.NewString(target));

        Table list = found.Table;
        Assert.Equal(1, list.Length);
        Table match = list.Get(1).Table;
        Assert.Equal("Bob", match.Get("name").String);
        Assert.Equal("76561198000000002", match.Get("steamId").String);
        Assert.Equal(_lua.DoString("return __channels[2]").Table, match.Get("channel").Table);
    }

    [Fact]
    public void PlayerLookup_FindOnline_ReturnsEveryPlayerWithTheSameName()
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Sam'),
            MakeChannel('76561198000000002', 'sam'),
            MakeChannel('76561198000000003', 'Other') }");
        DynValue lookup = Require("modules.player_lookup");

        DynValue found = _lua.Call(lookup.Table.Get("FindOnline"), DynValue.NewString("SAM"));

        Assert.Equal(2, found.Table.Length);
    }

    [Theory]
    [InlineData("Nobody")]
    [InlineData("Bo")]                     // a name must match completely
    [InlineData("76561190000000000")]
    [InlineData("")]
    public void PlayerLookup_FindOnline_ReturnsAnEmptyList_ForNoMatch(string target)
    {
        _lua.DoString("__channels = { MakeChannel('76561198000000002', 'Bob') }");
        DynValue lookup = Require("modules.player_lookup");

        DynValue found = _lua.Call(lookup.Table.Get("FindOnline"), DynValue.NewString(target));

        Assert.Equal(0, found.Table.Length);
    }

    [Fact]
    public void PlayerLookup_FindOnline_IgnoresTheClassDefaultObject_ByName()
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Ghost', 'PlayerRpcChannel /Script/SCUM.Default__PlayerRpcChannel') }");
        DynValue lookup = Require("modules.player_lookup");

        Assert.Equal(0, _lua.Call(lookup.Table.Get("FindOnline"), DynValue.NewString("Ghost")).Table.Length);
    }

    [Fact]
    public void PlayerLookup_ListOnline_ListsEveryoneSortedByName_OncePerPlayer()
    {
        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000003', 'carol'),
            MakeChannel('76561198000000001', 'Bob'),
            MakeChannel('76561198000000002', 'Alice'),
            MakeChannel('76561198000000002', 'Alice') }");      // the same player twice (a stale channel)
        DynValue lookup = Require("modules.player_lookup");

        Table list = _lua.Call(lookup.Table.Get("ListOnline")).Table;

        Assert.Equal(3, list.Length);
        string[] names = Enumerable.Range(1, 3).Select(i => list.Get(i).Table.Get("name").String).ToArray();
        Assert.Equal(new[] { "Alice", "Bob", "carol" }, names);          // letter case ignored when sorting
        Assert.Equal("76561198000000002", list.Get(1).Table.Get("steamId").String);
    }

    [Fact]
    public void PlayerLookup_ListOnline_IsEmpty_WhenNobodyIsOnline_OrOnlyTheDefaultObjectExists()
    {
        DynValue lookup = Require("modules.player_lookup");
        DynValue list = lookup.Table.Get("ListOnline");

        _lua.DoString("__channels = {}");
        Assert.Equal(0, _lua.Call(list).Table.Length);

        _lua.DoString(@"__channels = {
            MakeChannel('76561198000000001', 'Ghost', 'PlayerRpcChannel /Script/SCUM.Default__PlayerRpcChannel') }");
        Assert.Equal(0, _lua.Call(list).Table.Length);
    }

    [Fact]
    public void TheBridge_UsesThePlayerListOfTheMod_ForPlayers()
    {
        _lua.DoString("__channels = { MakeChannel('76561198000000009', 'Carol') }");
        DynValue bridge = Require("modules.rcon_bridge");

        Table list = _lua.Call(bridge.Table.Get("listPlayers")).Table;

        Assert.Equal(1, list.Length);
        Assert.Equal("Carol", list.Get(1).Table.Get("name").String);
    }

    [Theory]
    [InlineData("76561198000000009")]
    [InlineData("carol")]
    public void TheBridge_UsesThePlayerLookupOfTheMod_ForSayPm(string target)
    {
        _lua.DoString("__channels = { MakeChannel('76561198000000009', 'Carol') }");
        DynValue bridge = Require("modules.rcon_bridge");

        DynValue found = _lua.Call(bridge.Table.Get("findPlayers"), DynValue.NewString(target));

        Assert.Equal(1, found.Table.Length);
        Assert.Equal("Carol", found.Table.Get(1).Table.Get("name").String);
    }
}
