using ScumRcon.Config;
using ScumRcon.Core;
using ScumRcon.GameBridge;
using ScumRcon.Logging;
using ScumRcon.Security;

namespace ScumRcon;

public static class Program
{
    private const string DefaultConfigPath = "config/rcon.json";

    public static async Task<int> Main(string[] args)
    {
        string configPath = DefaultConfigPath;
        bool hashMode = false;
        bool consoleMode = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--console":
                    consoleMode = true;
                    break;
                case "--hash-password":
                    hashMode = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        if (hashMode) return HashPassword();

        if (!File.Exists(configPath))
        {
            ServerConfig fresh = ConfigLoader.CreateFirstRun(out string adminPassword);
            ConfigLoader.Save(fresh, configPath);
            Console.WriteLine($"First run: created {Path.GetFullPath(configPath)}");
            Console.WriteLine();
            Console.WriteLine("  Login : admin:<password>   (RCON password field = admin:" + adminPassword + ")");
            Console.WriteLine();
            Console.WriteLine("  This password is shown ONCE and is stored only as a hash. Save it now.");
            Console.WriteLine("  The bridge secret in the file must also be given to the in-game plugin.");
            Console.WriteLine("  Edit the file (ipAllowlist, users, commandRoles), then start again.");
            return 0;
        }

        ServerConfig config;
        try
        {
            config = ConfigLoader.Load(configPath);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Cannot read {configPath}: {ex.Message}");
            return 1;
        }

        IReadOnlyList<string> problems = ConfigLoader.Validate(config);
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("The configuration has problems, so the server will not start:");
            foreach (string p in problems) Console.Error.WriteLine("  - " + p);
            return 1;
        }

        string bridgeLine, bridgeShort;
        string? bridgeWarning;
        if (config.Bridge.TryGetMode(out BridgeMode mode) && mode == BridgeMode.File)
        {
            foreach (string folder in new[] { config.Bridge.CommandsDirectory, config.Bridge.ResponsesDirectory })
            {
                if (!Directory.Exists(folder))
                {
                    Console.Error.WriteLine($"The bridge folder does not exist: {folder}");
                    Console.Error.WriteLine("Start the game once with the ScumRcon mod loaded (it creates the folders), or create them.");
                    return 1;
                }
            }
            bridgeLine = "Bridge: files. Only trusted accounts should be able to write to " + config.Bridge.CommandsDirectory;
            bridgeShort = "files   (a folder the game mod watches)";
            bridgeWarning = "Only trusted accounts should be able to write to " + config.Bridge.CommandsDirectory;
        }
        else
        {
            bridgeLine = $"Bridge: TCP to {config.Bridge.Host}:{config.Bridge.Port}";
            bridgeShort = $"tcp     {config.Bridge.Host}:{config.Bridge.Port}";
            bridgeWarning = null;
        }

        FileAuditSink? audit = TryOpenAuditLog(config.AuditLogPath, out string? auditProblem);
        if (audit is null)
        {
            Console.Error.WriteLine(auditProblem);
            return 1;
        }
        using var auditOwner = audit;

        IGameBridge bridge = GameBridgeFactory.Create(config.Bridge);
        await using var server = new RconServer(config, bridge, audit);
        try
        {
            server.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Console.Error.WriteLine(DescribeListenProblem(config, ex));
            (bridge as IDisposable)?.Dispose();
            return 1;
        }
        // Colours and the logo only for the one-window mode in a real terminal; services and pipes get plain text.
        ConsoleTheme theme = consoleMode ? ConsoleTheme.Detect() : ConsoleTheme.Plain;
        PrintStartup(Console.Out, theme, bridgeLine, bridgeShort, bridgeWarning, server.LocalEndpoint.ToString(),
                     Path.GetFullPath(config.AuditLogPath));

        using var exit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.Cancel();
        };
        try
        {
            if (consoleMode)
            {
                // One window: the server keeps serving other RCON clients while you type commands here.
                var policy = new CommandPolicy(new Dictionary<string, Role>(), config.MaxCommandLength);
                var shell = new ConsoleShell(bridge, audit, policy, theme);
                Task<ShellEnd> typing = shell.RunAsync(Console.In, Console.Out, exit.Token);
                // Console input cannot be interrupted, so also wait for Ctrl+C on its own.
                Task finished = await Task.WhenAny(typing, Task.Delay(Timeout.Infinite, exit.Token)).ConfigureAwait(false);
                if (finished == typing && await typing.ConfigureAwait(false) == ShellEnd.EndOfInput)
                {
                    Console.WriteLine("No console input is available; the server keeps running. Press Ctrl+C to stop.");
                    await Task.Delay(Timeout.Infinite, exit.Token).ConfigureAwait(false);
                }
            }
            else
            {
                await Task.Delay(Timeout.Infinite, exit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine(theme.Kind("Stopping...", LineKind.Dim));
        (bridge as IDisposable)?.Dispose();
        return 0;
    }

    /// <summary>The start-up text. Plain mode prints exactly the two classic lines; colour mode adds the logo.</summary>
    internal static void PrintStartup(TextWriter output, ConsoleTheme theme, string bridgeLine, string bridgeShort,
                                      string? bridgeWarning, string endpoint, string auditPath)
    {
        if (!theme.Colors)
        {
            output.WriteLine(bridgeLine);
            output.WriteLine($"RCON server listening on {endpoint}. Press Ctrl+C to stop.");
            return;
        }

        static string Row(ConsoleTheme t, string label, string value) =>
            "   " + t.Kind(label.PadRight(10), LineKind.Dim) + t.Paint(value, 235, 235, 245);

        IReadOnlyList<string> logo = theme.Logo();
        int logoWidth = ConsoleTheme.LogoLines()[0].Length;

        output.WriteLine();
        foreach (string line in logo) output.WriteLine("  " + line);
        // the credit sits under the right end of the logo, in the logo's colours turned around
        output.WriteLine("  " + new string(' ', Math.Max(0, logoWidth - Credit.Length)) +
                         theme.Gradient(Credit, (255, 90, 200), (0, 225, 255)));
        output.WriteLine();
        output.WriteLine("   " + theme.Gradient("Remote console for SCUM dedicated servers", (0, 225, 255), (255, 90, 200)) +
                         theme.Kind("   early alpha", LineKind.Dim));
        output.WriteLine();
        output.WriteLine(Row(theme, "listening", endpoint));
        output.WriteLine(Row(theme, "bridge", bridgeShort));
        output.WriteLine(Row(theme, "audit log", auditPath));
        if (bridgeWarning is not null)
        {
            output.WriteLine();
            output.WriteLine("   " + theme.Kind("! " + bridgeWarning, LineKind.Warning));
        }
        output.WriteLine();
        output.WriteLine("   " + theme.Kind("Press Ctrl+C or type exit to stop.", LineKind.Dim));
        output.WriteLine();
    }

    /// <summary>Shown under the logo.</summary>
    internal const string Credit = "By TIMESKIP";

    /// <summary>
    /// Opens the audit log. If it is in use, the usual reason is a second copy of this server (for example
    /// one that was left running in another window), so say that instead of showing a stack trace.
    /// </summary>
    internal static FileAuditSink? TryOpenAuditLog(string path, out string? problem)
    {
        try
        {
            problem = null;
            return new FileAuditSink(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"Cannot open the audit log {Path.GetFullPath(path)}: {ex.Message}" + Environment.NewLine +
                      "Is another ScumRcon.Server already running with this configuration? " +
                      "Close it first (Task Manager, or type exit in its window), then start again. " +
                      "You do not need to restart it when the game server restarts.";
            return null;
        }
    }

    internal static string DescribeListenProblem(ServerConfig config, System.Net.Sockets.SocketException ex)
    {
        string where = $"{config.ListenAddress}:{config.Port}";
        return ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse
            ? $"Cannot listen on {where}: the port is already in use. Another ScumRcon.Server (or another program) is " +
              "listening there. Close it, or change \"port\" in the configuration."
            : $"Cannot listen on {where}: {ex.Message}";
    }

    private static int HashPassword()
    {
        Console.Error.Write("Password to hash (it will be visible): ");
        string password = CleanPastedPassword(Console.ReadLine());
        if (password.Length == 0)
        {
            Console.Error.WriteLine("No password given.");
            return 1;
        }
        Console.WriteLine(PasswordHasher.Hash(password));
        return 0;
    }

    /// <summary>
    /// Removes what a shell or editor may add around piped text: an invisible byte-order mark
    /// (U+FEFF) at the start and line-end characters at the end. Without this, a hash made from
    /// piped input would not match the password the admin later types.
    /// </summary>
    internal static string CleanPastedPassword(string? raw)
    {
        if (raw is null) return string.Empty;
        return raw.TrimStart('﻿').TrimEnd('\r', '\n');
    }

    private static void PrintHelp()
    {
        Console.WriteLine("ScumRcon.Server [--config <file>] [--console] [--hash-password]");
        Console.WriteLine("  --config <file>   configuration file (default " + DefaultConfigPath + ")");
        Console.WriteLine("  --console         also show a command prompt in this window (no second window needed)");
        Console.WriteLine("  --hash-password   read a password and print a hash for the config file");
    }
}
