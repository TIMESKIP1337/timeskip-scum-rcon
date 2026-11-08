using ScumRcon.Config;

namespace ScumRcon.Server.Tests;

/// <summary>
/// What a Viewer may do by default. The game's own list commands (ListFlags, ListSpawnedVehicles, ...) print their
/// text into the chat of an online player, and it never comes back over RCON, so a Viewer must not trigger them.
/// </summary>
public class DefaultRolesTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("players")]
    [InlineData("ListPlayers")]          // answered by the mod, never sent to the game
    public void AViewer_MayUse_TheseCommands(string command)
    {
        var roles = ServerConfig.DefaultCommandRoles();

        Assert.Equal("Viewer", roles[command]);
    }

    [Theory]
    [InlineData("ListFlags")]
    [InlineData("ListSpawnedVehicles")]
    [InlineData("DumpAllSquadsInfoList")]
    [InlineData("SpawnItem")]
    public void ThereIsNoViewerRule_ForTheseCommands_SoTheyNeedAdmin(string command)
    {
        var roles = ServerConfig.DefaultCommandRoles();

        Assert.False(roles.ContainsKey(command));
    }

    [Fact]
    public void TheExampleConfiguration_AgreesWithTheDefaults()
    {
        string text = File.ReadAllText(FindExample());

        Assert.Contains("\"players\": \"Viewer\"", text);
        Assert.DoesNotContain("\"ListFlags\"", text);
        Assert.DoesNotContain("\"ListSpawnedVehicles\"", text);
        Assert.DoesNotContain("\"DumpAllSquadsInfoList\"", text);
    }

    private static string FindExample()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "config", "rcon.example.json")))
            dir = Path.GetDirectoryName(dir);
        return dir is null
            ? throw new FileNotFoundException("config/rcon.example.json not found above the test folder")
            : Path.Combine(dir, "config", "rcon.example.json");
    }
}
