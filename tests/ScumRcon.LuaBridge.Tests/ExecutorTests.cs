using ScumRcon.GameBridge;

namespace ScumRcon.LuaBridge.Tests;

/// <summary>
/// Which player runs a game command. The game prints a command's text into the chat of the player who runs it, so
/// only a player from the game's admin list (AdminUsers.ini) may be used, and the command is refused when no admin
/// is online. Say-style commands (say, saypm, players) need no admin.
/// </summary>
public sealed class ExecutorTests : IDisposable
{
    private const string Alice = "76561198000000001";     // a normal player
    private const string Bob = "76561198000000002";       // an admin
    private const string Cara = "76561198000000003";      // an admin

    private readonly LuaGame _game = new();
    private readonly FileGameBridge _bridge;

    public ExecutorTests()
    {
        _bridge = new FileGameBridge(_game.Commands, _game.Responses, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));
        _game.SetExecutorMode("admin");
        _game.AdminUsersText = $"{Bob}[Godmode]\r\n{Cara}\r\n";
    }

    public void Dispose() => _game.Dispose();

    /// <summary>Alice (channel 1, not an admin), Bob (channel 2, admin), Cara (channel 3, admin).</summary>
    private void AllThreeOnline()
    {
        _game.AddPlayerChannel();
        _game.AddPlayerChannel();
        _game.Players[Alice] = (1, "Alice");
        _game.Players[Bob] = (2, "Bob");
        _game.Players[Cara] = (3, "Cara");
    }

    private Task<BridgeResult> Run(string command) => _game.RunUntil(_bridge.ExecuteAsync(command, CancellationToken.None));

    [Fact]
    public async Task ACommand_RunsOnAnAdminsChannel_NotOnTheFirstPlayer()
    {
        AllThreeOnline();

        BridgeResult r = await Run("ListFlags");

        Assert.True(r.Success, r.Text);
        Assert.Equal(new[] { "ListFlags" }, _game.AdminCommands.ToArray());
        Assert.Equal(new[] { 2 }, _game.AdminCommandAddresses.ToArray());          // Bob, not Alice
        Assert.Contains(_game.Printed, line => line.Contains("Running as admin Bob") && line.Contains(Bob));
    }

    [Fact]
    public async Task WithSeveralAdmins_TheFirstOneInTheList_RunsIt()
    {
        AllThreeOnline();

        await Run("ListFlags");
        await Run("announce hi");

        Assert.Equal(new[] { 2, 2 }, _game.AdminCommandAddresses.ToArray());
    }

    [Fact]
    public async Task WhenNoAdminIsOnline_TheCommandIsRefused_AndNeverReachesTheGame()
    {
        _game.Players[Alice] = (1, "Alice");              // only a normal player is online

        BridgeResult r = await Run("ListFlags");

        Assert.False(r.Success);
        Assert.Contains("No admin player is online", r.Text);
        Assert.Empty(_game.AdminCommands);
        Assert.Empty(_game.Deliveries);                   // and nothing was written to anybody's chat
    }

    [Fact]
    public async Task WhenNobodyIsOnline_TheCommandIsRefused()
    {
        BridgeResult r = await Run("announce hi");

        Assert.False(r.Success);
        Assert.Contains("No admin player is online", r.Text);
        Assert.Empty(_game.AdminCommands);
    }

    [Fact]
    public async Task AnAdminWhoseChannelIsGone_IsNotUsed()
    {
        _game.Players[Bob] = (1, "Bob");
        _game.AllChannelsInvalid = true;

        BridgeResult r = await Run("ListFlags");

        Assert.False(r.Success);
        Assert.Contains("No admin player is online", r.Text);
        Assert.Empty(_game.AdminCommands);
    }

    [Fact]
    public async Task WhenTheAdminListCannotBeRead_TheCommandIsRefused_WithAHintHowToFixIt()
    {
        AllThreeOnline();
        _game.AdminUsersText = null;                      // the file is missing

        BridgeResult r = await Run("ListFlags");

        Assert.False(r.Success);
        Assert.Contains("Cannot tell who the admins are", r.Text);
        Assert.Contains("AdminUsers.ini", r.Text);
        Assert.Contains("RCON_ADMIN_USERS_FILE", r.Text);
        Assert.Empty(_game.AdminCommands);
    }

    [Fact]
    public async Task AnEmptyAdminList_MeansNobodyMayRunCommands()
    {
        AllThreeOnline();
        _game.AdminUsersText = "";

        BridgeResult r = await Run("ListFlags");

        Assert.False(r.Success);
        Assert.Contains("No admin player is online", r.Text);
    }

    [Theory]
    [InlineData("76561198000000002[Godmode]")]                                   // the usual line
    [InlineData("76561198000000002")]                                            // without the part in brackets
    [InlineData("  76561198000000002  [Godmode]  ")]                             // spaces around
    [InlineData("76561198000000009[Godmode]\n76561198000000002[Godmode]\n")]     // not the first line, LF only
    [InlineData("\r\n\r\n76561198000000002\r\n\r\n")]                            // blank lines
    [InlineData("junk\r\n# comment\r\n76561198000000002[Godmode]")]              // lines that are not IDs
    [InlineData("ï»¿76561198000000002[Godmode]")]                 // a UTF-8 byte order mark (as bytes)
    public async Task TheAdminList_IsReadTolerantly(string fileText)
    {
        AllThreeOnline();
        _game.AdminUsersText = fileText;

        BridgeResult r = await Run("ListFlags");

        Assert.True(r.Success, r.Text);
        Assert.Equal(new[] { 2 }, _game.AdminCommandAddresses.ToArray());      // Bob is the admin in every variant
    }

    [Theory]
    [InlineData("12345[Godmode]")]                    // too short to be a SteamID
    [InlineData("Bob")]                               // names are not accepted, only SteamIDs
    [InlineData("76561198000000099")]                 // somebody who is not online
    public async Task ALineThatIsNotAnOnlineAdminsSteamId_MakesNobodyAnAdmin(string fileText)
    {
        AllThreeOnline();
        _game.AdminUsersText = fileText;

        BridgeResult r = await Run("ListFlags");

        Assert.False(r.Success);
        Assert.Empty(_game.AdminCommands);
    }

    [Fact]
    public async Task TheReply_IsCaptured_FromTheAdminChannel()
    {
        AllThreeOnline();
        _game.OnAdminCommand = (cmd, ch) => _game.Chat(ch, "reply to " + cmd, 4);

        BridgeResult r = await Run("announce hi");

        Assert.Equal("reply to announce hi", r.Text);
    }

    // ── the say-style commands need no admin ─────────────────────────────────────────────────────
    [Fact]
    public async Task Players_Say_AndSayPm_WorkWithoutAnyAdminOnline()
    {
        _game.Players[Alice] = (1, "Alice");              // a normal player only

        BridgeResult players = await Run("players");
        BridgeResult say = await Run("say global hello");
        BridgeResult saypm = await Run("saypm Alice 4 hi");

        Assert.True(players.Success, players.Text);
        Assert.StartsWith("1 player online:", players.Text);
        Assert.True(say.Success, say.Text);
        Assert.True(saypm.Success, saypm.Text);
        Assert.Empty(_game.AdminCommands);
    }

    // ── the way out, for servers whose admin list is somewhere unusual ────────────────────────────
    [Fact]
    public async Task InAnyMode_TheFirstPlayerFound_RunsTheCommand_EvenWithoutAdmins()
    {
        _game.SetExecutorMode("any");
        _game.AdminUsersText = null;

        BridgeResult r = await Run("ListFlags");

        Assert.True(r.Success, r.Text);
        Assert.Equal(new[] { 1 }, _game.AdminCommandAddresses.ToArray());
    }

    [Theory]
    [InlineData("any", "any")]
    [InlineData("ANY", "any")]
    [InlineData("admin", "admin")]
    [InlineData("", "admin")]                         // anything else means the safe mode
    [InlineData("everybody", "admin")]
    public async Task TheMode_AnythingButAny_MeansAdmin(string setting, string expected)
    {
        _game.SetExecutorMode(setting);
        AllThreeOnline();

        await Run("ListFlags");

        int expectedAddress = expected == "any" ? 1 : 2;
        Assert.Equal(new[] { expectedAddress }, _game.AdminCommandAddresses.ToArray());
    }
}
