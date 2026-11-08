using System.Runtime.InteropServices;
using System.Text;

namespace ScumRcon.Core;

/// <summary>Meaning of a line of console output; the theme turns it into a colour.</summary>
public enum LineKind
{
    Plain,
    Success,
    Error,
    Warning,
    Info,
    Dim
}

/// <summary>
/// Colours for the server window. Two modes: <see cref="Plain"/> (text unchanged, used when output goes to a file,
/// a pipe or a service) and ANSI true colour (used in a real terminal). Only ASCII characters are ever added to
/// the text, so the window looks right whatever the console font or code page is.
/// </summary>
public sealed class ConsoleTheme
{
    private const string Reset = "\u001b[0m";

    public static readonly ConsoleTheme Plain = new(false);
    public static readonly ConsoleTheme Ansi = new(true);

    private ConsoleTheme(bool colors) => Colors = colors;

    public bool Colors { get; }

    /// <summary>Chooses the mode: colours only for an interactive terminal that understands them.</summary>
    internal static ConsoleTheme Choose(bool outputRedirected, string? noColorVariable, bool terminalSupportsAnsi)
    {
        if (outputRedirected) return Plain;
        if (!string.IsNullOrEmpty(noColorVariable)) return Plain;     // https://no-color.org
        return terminalSupportsAnsi ? Ansi : Plain;
    }

    /// <summary>The theme for the real console of this process.</summary>
    public static ConsoleTheme Detect()
    {
        bool supported = !OperatingSystem.IsWindows() || TryEnableWindowsVirtualTerminal();
        return Choose(Console.IsOutputRedirected, Environment.GetEnvironmentVariable("NO_COLOR"), supported);
    }

    // ── painting ──────────────────────────────────────────────────────────────────────────────
    public string Paint(string text, int r, int g, int b) =>
        Colors && text.Length > 0 ? $"\u001b[38;2;{r};{g};{b}m{text}{Reset}" : text;

    public string Bold(string text, int r, int g, int b) =>
        Colors && text.Length > 0 ? $"\u001b[1;38;2;{r};{g};{b}m{text}{Reset}" : text;

    public string Kind(string text, LineKind kind) => kind switch
    {
        LineKind.Success => Paint(text, 90, 230, 130),
        LineKind.Error => Paint(text, 255, 100, 100),
        LineKind.Warning => Paint(text, 255, 205, 80),
        LineKind.Info => Paint(text, 100, 205, 255),
        LineKind.Dim => Paint(text, 140, 145, 160),
        _ => text
    };

    /// <summary>Colours every character along a straight line between two colours.</summary>
    public string Gradient(string text, (int R, int G, int B) from, (int R, int G, int B) to)
    {
        if (!Colors || text.Length == 0) return text;
        var sb = new StringBuilder(text.Length * 20);
        int last = Math.Max(1, text.Length - 1);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                sb.Append(' ');
                continue;
            }
            double t = (double)i / last;
            int r = (int)Math.Round(from.R + (to.R - from.R) * t);
            int g = (int)Math.Round(from.G + (to.G - from.G) * t);
            int b = (int)Math.Round(from.B + (to.B - from.B) * t);
            sb.Append($"\u001b[38;2;{r};{g};{b}m").Append(text[i]);
        }
        return sb.Append(Reset).ToString();
    }

    // ── the logo ──────────────────────────────────────────────────────────────────────────────
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['S'] = new[] { @" ____  ", @"/ ___| ", @"\___ \ ", @" ___) |", @"|____/ " },
        ['C'] = new[] { @"  ____ ", @" / ___|", @"| |    ", @"| |___ ", @" \____|" },
        ['U'] = new[] { @" _   _ ", @"| | | |", @"| | | |", @"| |_| |", @" \___/ " },
        ['M'] = new[] { @" __  __ ", @"|  \/  |", @"| |\/| |", @"| |  | |", @"|_|  |_|" },
        ['R'] = new[] { @" ____  ", @"|  _ \ ", @"| |_) |", @"|  _ < ", @"|_| \_\" },
        ['O'] = new[] { @"  ___  ", @" / _ \ ", @"| | | |", @"| |_| |", @" \___/ " },
        ['N'] = new[] { @" _   _ ", @"| \ | |", @"|  \| |", @"| |\  |", @"|_| \_|" },
        [' '] = new[] { "   ", "   ", "   ", "   ", "   " },
    };

    /// <summary>The logo as plain ASCII text, five lines of equal width.</summary>
    public static IReadOnlyList<string> LogoLines(string word = "SCUM RCON")
    {
        var rows = new StringBuilder[5];
        for (int i = 0; i < rows.Length; i++) rows[i] = new StringBuilder();
        foreach (char c in word.ToUpperInvariant())
        {
            string[] glyph = Glyphs.TryGetValue(c, out var g) ? g : Glyphs[' '];
            int width = glyph.Max(row => row.Length);
            for (int i = 0; i < rows.Length; i++) rows[i].Append(glyph[i].PadRight(width));
        }
        return rows.Select(r => r.ToString().TrimEnd()).Select(r => r.PadRight(rows.Max(x => x.Length))).ToArray();
    }

    /// <summary>The logo in colour: cyan to pink along each line, a little further along on every line.</summary>
    public IReadOnlyList<string> Logo(string word = "SCUM RCON")
    {
        IReadOnlyList<string> lines = LogoLines(word);
        var painted = new List<string>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var from = (R: 0 + i * 40, G: 225 - i * 25, B: 255 - i * 10);
            var to = (R: 255 - (lines.Count - 1 - i) * 20, G: 70 + i * 15, B: 205);
            painted.Add(Gradient(lines[i], from, to));
        }
        return painted;
    }

    // ── making the terminal understand colours (Windows only) ─────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private static bool TryEnableWindowsVirtualTerminal()
    {
        try
        {
            const int StdOutputHandle = -11;
            const uint EnableVirtualTerminalProcessing = 0x0004;
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out uint mode)) return false;
            return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
