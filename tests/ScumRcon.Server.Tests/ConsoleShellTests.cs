using ScumRcon.Core;
using ScumRcon.GameBridge;
using ScumRcon.Security;

namespace ScumRcon.Server.Tests;

public sealed class ConsoleShellTests
{
    private readonly FakeBridge _bridge = new();
    private readonly MemoryAudit _audit = new();
    private readonly ConsoleShell _shell;

    public ConsoleShellTests()
    {
        _shell = new ConsoleShell(_bridge, _audit, new CommandPolicy(new Dictionary<string, Role>(), 64));
    }

    private async Task<(ShellEnd End, string Output)> Run(string typed, CancellationToken ct = default)
    {
        var output = new StringWriter();
        ShellEnd end = await _shell.RunAsync(new StringReader(typed), output, ct);
        return (end, output.ToString());
    }

    [Fact]
    public async Task ATypedCommand_GoesToTheGame_AndTheAnswerIsShown()
    {
        var (end, output) = await Run("ListPlayers\n");

        Assert.Equal(new[] { "ListPlayers" }, _bridge.Commands.ToArray());
        Assert.Contains("ok:ListPlayers", output);
        Assert.Equal(ShellEnd.EndOfInput, end);          // the input simply ended after the command
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("quit")]
    [InlineData("  EXIT  ")]
    [InlineData("Quit")]
    public async Task ExitAndQuit_EndTheShell_WithoutSendingAnythingToTheGame(string word)
    {
        var (end, _) = await Run(word + "\nListPlayers\n");

        Assert.Equal(ShellEnd.ExitRequested, end);
        Assert.Empty(_bridge.Commands);                  // nothing after "exit" is read
    }

    [Fact]
    public async Task ABomInFrontOfTheFirstCommand_IsNotPartOfTheCommand()
    {
        await Run("﻿ListPlayers\n﻿exit\n");

        Assert.Equal(new[] { "ListPlayers" }, _bridge.Commands.ToArray());
        Assert.Contains("\"command\":\"ListPlayers\"", Assert.Single(_audit.Lines));
    }

    [Fact]
    public async Task EmptyLines_AreIgnored()
    {
        await Run("\n   \nListPlayers\n\n");

        Assert.Equal(new[] { "ListPlayers" }, _bridge.Commands.ToArray());
    }

    [Fact]
    public async Task SeveralCommands_RunInOrder_EachWithItsOwnAnswer()
    {
        var (_, output) = await Run("say global hi\n#SpawnItem Weapon_AKM 1 76561198000000042 Location\n");

        Assert.Equal(new[] { "say global hi", "#SpawnItem Weapon_AKM 1 76561198000000042 Location" }, _bridge.Commands.ToArray());
        Assert.True(output.IndexOf("ok:say global hi", StringComparison.Ordinal) < output.IndexOf("ok:#SpawnItem", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGameError_IsShownAsAnError()
    {
        _bridge.Reply = _ => new BridgeResult(false, "Failed to execute");

        var (_, output) = await Run("ListPlayers\n");

        Assert.Contains("Error: Failed to execute", output);
    }

    [Fact]
    public async Task ATooLongCommand_IsDenied_AndNeverSent()
    {
        var (_, output) = await Run(new string('x', 65) + "\n");

        Assert.Contains("Denied:", output);
        Assert.Empty(_bridge.Commands);
        Assert.Contains("cmd_denied", _audit.Events);
    }

    [Fact]
    public async Task AControlCharacter_IsDenied_AndNeverSent()
    {
        var (_, output) = await Run("List\u0007Players\n");

        Assert.Contains("Denied:", output);
        Assert.Empty(_bridge.Commands);
    }

    [Fact]
    public async Task EveryCommand_IsAudited_AsTheConsoleUser_WithoutPasswords()
    {
        await Run("ListPlayers\n");

        string line = Assert.Single(_audit.Lines);
        Assert.StartsWith("cmd ", line);
        Assert.Contains("\"user\":\"console\"", line);
        Assert.Contains("\"role\":\"Admin\"", line);
        Assert.Contains("\"ip\":\"local\"", line);
        Assert.Contains("\"command\":\"ListPlayers\"", line);
        Assert.Contains("\"success\":true", line);
    }

    [Fact]
    public async Task ThePromptIsShown_BeforeEachRead()
    {
        var (_, output) = await Run("ListPlayers\n");

        Assert.Equal(2, output.Split(ConsoleShell.Prompt).Length - 1);   // once before the command, once at the end
    }

    [Fact]
    public async Task ACancelledToken_EndsTheShell()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (end, _) = await Run("ListPlayers\n", cts.Token);

        Assert.Equal(ShellEnd.Cancelled, end);
        Assert.Empty(_bridge.Commands);
    }
}
