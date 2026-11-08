using ScumRcon.Core;
using ScumRcon.Security;
using Xunit.Abstractions;

namespace ScumRcon.Server.Tests;

public sealed class ConsoleThemeTests
{
    private readonly ITestOutputHelper _output;

    public ConsoleThemeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Plain_ChangesNothing()
    {
        ConsoleTheme t = ConsoleTheme.Plain;

        Assert.Equal("hello", t.Paint("hello", 1, 2, 3));
        Assert.Equal("hello", t.Bold("hello", 1, 2, 3));
        Assert.Equal("hello", t.Gradient("hello", (0, 0, 0), (255, 255, 255)));
        foreach (LineKind kind in Enum.GetValues<LineKind>()) Assert.Equal("hello", t.Kind("hello", kind));
        Assert.Equal(Enumerable.Range(0, 5).Select(_ => false), ConsoleTheme.Plain.Logo().Select(l => l.Contains('\u001b')));
    }

    [Fact]
    public void Ansi_WrapsTextInColourCodes_AndAlwaysResets()
    {
        string painted = ConsoleTheme.Ansi.Paint("ok", 10, 20, 30);

        Assert.Equal("\u001b[38;2;10;20;30mok\u001b[0m", painted);
        Assert.EndsWith("\u001b[0m", ConsoleTheme.Ansi.Gradient("abc", (0, 0, 0), (255, 255, 255)));
        Assert.EndsWith("\u001b[0m", ConsoleTheme.Ansi.Bold("x", 1, 2, 3));
    }

    [Fact]
    public void Ansi_EmptyText_StaysEmpty()
    {
        Assert.Equal("", ConsoleTheme.Ansi.Paint("", 1, 2, 3));
        Assert.Equal("", ConsoleTheme.Ansi.Gradient("", (0, 0, 0), (255, 255, 255)));
    }

    [Fact]
    public void Gradient_GoesFromTheFirstColourToTheLast_AndKeepsTheText()
    {
        string g = ConsoleTheme.Ansi.Gradient("abc", (0, 0, 0), (200, 100, 50));

        Assert.Contains("38;2;0;0;0m", g);          // first character: start colour
        Assert.Contains("38;2;200;100;50m", g);     // last character: end colour
        Assert.Equal("abc", System.Text.RegularExpressions.Regex.Replace(g, "\u001b\\[[0-9;]*m", ""));
    }

    [Theory]
    [InlineData(false, null, true, true)]       // a real terminal that understands colours
    [InlineData(true, null, true, false)]       // output goes to a file or a pipe
    [InlineData(false, "1", true, false)]       // NO_COLOR is set
    [InlineData(false, "", true, true)]         // an empty NO_COLOR does not count
    [InlineData(false, null, false, false)]     // the terminal does not understand colours
    public void TheModeIsChosenFromTheEnvironment(bool redirected, string? noColor, bool ansiSupported, bool expectColors)
    {
        Assert.Equal(expectColors, ConsoleTheme.Choose(redirected, noColor, ansiSupported).Colors);
    }

    [Fact]
    public void TheLogo_IsPlainAscii_FiveEqualLines_AndSaysScumRcon()
    {
        IReadOnlyList<string> lines = ConsoleTheme.LogoLines();

        foreach (string line in lines) _output.WriteLine(line);         // so the shape can be looked at

        Assert.Equal(5, lines.Count);
        Assert.Single(lines.Select(l => l.Length).Distinct());
        Assert.All(lines, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));   // printable ASCII only
        Assert.True(lines[0].Length is > 30 and < 90, "the logo must fit in a normal window");
    }

    [Fact]
    public void TheColouredLogo_HasTheSameShape_AsThePlainOne()
    {
        var plain = ConsoleTheme.LogoLines();
        var painted = ConsoleTheme.Ansi.Logo();

        Assert.Equal(plain.Count, painted.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            string stripped = System.Text.RegularExpressions.Regex.Replace(painted[i], "\u001b\\[[0-9;]*m", "");
            Assert.Equal(plain[i], stripped);
        }
    }

    [Fact]
    public void AnUnknownLetter_BecomesASpace_NotACrash()
    {
        Assert.Equal(5, ConsoleTheme.LogoLines("S?Z").Count);
    }

    // ── the start-up screen ────────────────────────────────────────────────────────────────────
    private static string Strip(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", "");

    [Fact]
    public void TheStartupScreen_InColour_ShowsTheLogo_TheCredit_AndTheFacts()
    {
        var output = new StringWriter();

        Program.PrintStartup(output, ConsoleTheme.Ansi, "Bridge: files...", "files   (a folder the game mod watches)",
                             "Only trusted accounts should be able to write here", "127.0.0.1:28025", @"C:\x\audit.jsonl");

        string text = Strip(output.ToString());
        _output.WriteLine(text);
        Assert.Contains("By TIMESKIP", text);
        Assert.Contains("127.0.0.1:28025", text);
        Assert.Contains(@"C:\x\audit.jsonl", text);
        Assert.Contains("Only trusted accounts", text);
        Assert.Contains(ConsoleTheme.LogoLines()[0].TrimEnd(), text);
        Assert.Contains("\u001b[", output.ToString());
    }

    [Fact]
    public void TheCredit_SitsUnderTheRightEndOfTheLogo()
    {
        var output = new StringWriter();

        Program.PrintStartup(output, ConsoleTheme.Ansi, "b", "s", null, "e", "a");

        string[] lines = Strip(output.ToString()).Split(Environment.NewLine);
        string creditLine = Assert.Single(lines, l => l.Contains("By TIMESKIP"));
        int logoWidth = ConsoleTheme.LogoLines()[0].Length;
        Assert.Equal(2 + logoWidth, creditLine.TrimEnd().Length);        // two spaces of margin + the logo width
    }

    [Fact]
    public void TheStartupScreen_InPlainMode_IsTheTwoClassicLines_WithoutLogoOrCredit()
    {
        var output = new StringWriter();

        Program.PrintStartup(output, ConsoleTheme.Plain, "Bridge: files.", "s", "w", "127.0.0.1:28025", "a");

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "Bridge: files.", "RCON server listening on 127.0.0.1:28025. Press Ctrl+C to stop." }, lines);
    }

    // ── the shell in colour ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TheShell_InColour_PaintsPromptSuccessAndErrors_WithoutChangingTheText()
    {
        var bridge = new FakeBridge();
        bridge.Reply = c => c == "bad" ? new ScumRcon.GameBridge.BridgeResult(false, "nope") : new ScumRcon.GameBridge.BridgeResult(true, "fine");
        var shell = new ConsoleShell(bridge, new MemoryAudit(), new CommandPolicy(new Dictionary<string, Role>(), 64), ConsoleTheme.Ansi);
        var output = new StringWriter();

        await shell.RunAsync(new StringReader("good\nbad\n"), output, CancellationToken.None);

        string text = output.ToString();
        Assert.Contains("\u001b[", text);
        string stripped = System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", "");
        Assert.Contains("rcon> ", stripped);
        Assert.Contains("fine", stripped);
        Assert.Contains("Error: nope", stripped);
        Assert.Contains("38;2;90;230;130mfine", text);          // success is green
        Assert.Contains("38;2;255;100;100mError: nope", text);  // errors are red
    }

    [Fact]
    public async Task TheShell_InPlainMode_AddsNoColourCodes()
    {
        var shell = new ConsoleShell(new FakeBridge(), new MemoryAudit(), new CommandPolicy(new Dictionary<string, Role>(), 64));
        var output = new StringWriter();

        await shell.RunAsync(new StringReader("ListPlayers\n"), output, CancellationToken.None);

        Assert.DoesNotContain("\u001b", output.ToString());
    }
}
