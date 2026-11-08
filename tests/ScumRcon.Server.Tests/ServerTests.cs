using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ScumRcon.Config;
using ScumRcon.Core;
using ScumRcon.GameBridge;
using ScumRcon.Logging;
using ScumRcon.Protocol;
using ScumRcon.Security;

namespace ScumRcon.Server.Tests;

internal sealed class FakeBridge : IGameBridge
{
    public readonly ConcurrentQueue<string> Commands = new();
    public Func<string, BridgeResult> Reply = c => new BridgeResult(true, "ok:" + c);

    public Task<BridgeResult> ExecuteAsync(string command, CancellationToken ct)
    {
        Commands.Enqueue(command);
        return Task.FromResult(Reply(command));
    }
}

internal sealed class MemoryAudit : IAuditSink
{
    public readonly ConcurrentQueue<string> Events = new();
    public readonly ConcurrentQueue<string> Lines = new();

    public void Write(string eventName, IReadOnlyDictionary<string, object?> fields)
    {
        Events.Enqueue(eventName);
        Lines.Enqueue(eventName + " " + JsonSerializer.Serialize(fields));
    }
}

internal sealed class TestClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream _stream = null!;

    public static async Task<TestClient> ConnectAsync(IPEndPoint endpoint)
    {
        var c = new TestClient();
        await c._tcp.ConnectAsync(endpoint.Address, endpoint.Port);
        c._stream = c._tcp.GetStream();
        return c;
    }

    public Task SendAsync(RconPacket p) => _stream.WriteAsync(p.Serialize()).AsTask();

    public Task SendRawAsync(byte[] bytes) => _stream.WriteAsync(bytes).AsTask();

    /// <summary>Reads one packet; null if the server closed the connection.</summary>
    public async Task<RconPacket?> ReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            return await RconPacket.ReadAsync(_stream, cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or RconProtocolException)
        {
            return null;
        }
    }

    public async Task<bool> LoginAsync(string credentials, int id = 1)
    {
        await SendAsync(new RconPacket(id, PacketType.Auth, credentials));
        RconPacket? a = await ReadAsync();
        if (a is null) return false;
        if (a.Value.Type == PacketType.ResponseValue)
        {
            RconPacket? b = await ReadAsync();
            return b is { Id: > 0 };
        }
        return a.Value.Id != -1;
    }

    public async Task<string> RunAsync(string command, int id = 5)
    {
        await SendAsync(new RconPacket(id, PacketType.ExecCommand, command));
        RconPacket? reply = await ReadAsync();
        return reply?.Body ?? "<closed>";
    }

    public void Dispose() => _tcp.Dispose();
}

public class ServerTests : IAsyncLifetime
{
    private const int TestIterations = 1000;

    private readonly FakeBridge _bridge = new();
    private readonly MemoryAudit _audit = new();
    private RconServer _server = null!;
    private ServerConfig _config = null!;

    public async Task InitializeAsync() => await StartAsync(_ => { });

    private async Task StartAsync(Action<ServerConfig> tweak)
    {
        if (_server is not null) await _server.DisposeAsync();

        _config = new ServerConfig
        {
            ListenAddress = "127.0.0.1",
            Port = 0,
            IpAllowlist = new List<string> { "127.0.0.1" },
            AuthTimeoutSeconds = 3,
            IdleTimeoutSeconds = 30,
            MaxFailedAuth = 3,
            MaxCommandsPerSecond = 50,
            Users =
            {
                new UserConfig { Name = "boss", Role = "Admin", PasswordHash = PasswordHasher.Hash("boss-pw", TestIterations) },
                new UserConfig { Name = "viewer", Role = "Viewer", PasswordHash = PasswordHasher.Hash("view-pw", TestIterations) }
            },
            CommandRoles = new Dictionary<string, string> { ["ListPlayers"] = "Viewer" }
        };
        tweak(_config);
        _server = new RconServer(_config, _bridge, _audit);
        _server.Start();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Login_ThenCommand_ReachesBridge()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        Assert.True(await c.LoginAsync("boss:boss-pw"));
        Assert.Equal("ok:ListPlayers", await c.RunAsync("ListPlayers"));
        Assert.Equal(new[] { "ListPlayers" }, _bridge.Commands.ToArray());
    }

    [Fact]
    public async Task WrongPassword_GetsMinusOne_AndConnectionCloses()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        await c.SendAsync(new RconPacket(9, PacketType.Auth, "boss:wrong"));
        RconPacket? reply = await c.ReadAsync();
        Assert.Equal(-1, reply!.Value.Id);
        Assert.Null(await c.ReadAsync());
        Assert.Contains("auth_fail", _audit.Events);
    }

    [Fact]
    public async Task UnknownUser_LooksTheSameAsWrongPassword()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        await c.SendAsync(new RconPacket(9, PacketType.Auth, "nobody:whatever"));
        Assert.Equal(-1, (await c.ReadAsync())!.Value.Id);
    }

    [Fact]
    public async Task LoginWithoutUserName_IsRefused()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        await c.SendAsync(new RconPacket(9, PacketType.Auth, "boss-pw"));
        Assert.Equal(-1, (await c.ReadAsync())!.Value.Id);
    }

    [Fact]
    public async Task Viewer_CannotRunAdminCommand_AndBridgeIsNotCalled()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        Assert.True(await c.LoginAsync("viewer:view-pw"));

        string denied = await c.RunAsync("SpawnItem Gold 100");
        Assert.StartsWith("Denied:", denied);
        Assert.Equal("ok:ListPlayers", await c.RunAsync("ListPlayers"));
        Assert.Equal(new[] { "ListPlayers" }, _bridge.Commands.ToArray());
        Assert.Contains("cmd_denied", _audit.Events);
    }

    [Fact]
    public async Task Newline_InCommand_IsDenied_EvenForAdmin()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        Assert.True(await c.LoginAsync("boss:boss-pw"));
        string reply = await c.RunAsync("ListPlayers\nSpawnItem Gold 1");
        Assert.StartsWith("Denied:", reply);
        Assert.Empty(_bridge.Commands);
    }

    [Fact]
    public async Task RepeatedFailures_BlockTheAddress()
    {
        for (int i = 0; i < 3; i++)
        {
            using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
            await c.SendAsync(new RconPacket(1, PacketType.Auth, "boss:nope"));
            await c.ReadAsync();
        }

        using var blocked = await TestClient.ConnectAsync(_server.LocalEndpoint);
        // even the right password is not heard now: the server just closes
        await blocked.SendAsync(new RconPacket(1, PacketType.Auth, "boss:boss-pw"));
        Assert.Null(await blocked.ReadAsync());
        Assert.Contains("blocked", _audit.Events);
    }

    [Fact]
    public async Task OversizedPacket_ClosesTheConnection()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 1_000_000);
        await c.SendRawAsync(header);
        Assert.Null(await c.ReadAsync());
        Assert.Contains("protocol_error", _audit.Events);
    }

    [Fact]
    public async Task AddressNotOnAllowlist_IsClosedImmediately()
    {
        await StartAsync(cfg => cfg.IpAllowlist = new List<string> { "203.0.113.5" });
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        await c.SendAsync(new RconPacket(1, PacketType.Auth, "boss:boss-pw"));
        Assert.Null(await c.ReadAsync());
        Assert.Contains("rejected_ip", _audit.Events);
    }

    [Fact]
    public async Task CommandBeforeLogin_ClosesTheConnection()
    {
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        await c.SendAsync(new RconPacket(1, PacketType.ExecCommand, "ListPlayers"));
        Assert.Null(await c.ReadAsync());
        Assert.Empty(_bridge.Commands);
    }

    [Fact]
    public async Task SilentClient_IsDroppedAfterTheAuthTimeout()
    {
        await StartAsync(cfg => cfg.AuthTimeoutSeconds = 1);
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        Assert.Null(await c.ReadAsync());
        Assert.Contains("auth_timeout", _audit.Events);
    }

    [Fact]
    public async Task LongOutput_IsSplitIntoSeveralPackets_AndKeepsTheRequestId()
    {
        _bridge.Reply = _ => new BridgeResult(true, new string('x', 10_000));
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        Assert.True(await c.LoginAsync("boss:boss-pw"));

        await c.SendAsync(new RconPacket(77, PacketType.ExecCommand, "ListPlayers"));
        var sb = new StringBuilder();
        int packets = 0;
        while (sb.Length < 10_000)
        {
            RconPacket p = (await c.ReadAsync())!.Value;
            Assert.Equal(77, p.Id);
            sb.Append(p.Body);
            packets++;
        }
        Assert.Equal(10_000, sb.Length);
        Assert.True(packets >= 3);
    }

    [Fact]
    public async Task BridgeFailure_IsReportedAsError()
    {
        _bridge.Reply = _ => new BridgeResult(false, "The game bridge is unavailable.");
        using var c = await TestClient.ConnectAsync(_server.LocalEndpoint);
        Assert.True(await c.LoginAsync("boss:boss-pw"));
        Assert.Equal("Error: The game bridge is unavailable.", await c.RunAsync("ListPlayers"));
    }

    [Fact]
    public async Task Passwords_NeverAppearInTheAuditLog()
    {
        using (var c = await TestClient.ConnectAsync(_server.LocalEndpoint))
        {
            await c.SendAsync(new RconPacket(1, PacketType.Auth, "boss:SuperSecretGuess123"));
            await c.ReadAsync();
        }
        using (var c = await TestClient.ConnectAsync(_server.LocalEndpoint))
        {
            Assert.True(await c.LoginAsync("boss:boss-pw"));
            await c.RunAsync("ListPlayers");
        }

        string all = string.Join("\n", _audit.Lines);
        Assert.DoesNotContain("SuperSecretGuess123", all);
        Assert.DoesNotContain("boss-pw", all);
    }
}

public class TcpGameBridgeTests
{
    private static Task<(TcpListener Listener, Task Server)> StartFakeGame(
        Func<JsonElement, string> respond, int requests = 1)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            for (int i = 0; i < requests; i++)
            {
                byte[] header = new byte[4];
                await stream.ReadExactlyAsync(header);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                await stream.ReadExactlyAsync(body);
                using JsonDocument doc = JsonDocument.Parse(body);
                byte[] reply = Encoding.UTF8.GetBytes(respond(doc.RootElement.Clone()));
                byte[] frame = new byte[4 + reply.Length];
                BinaryPrimitives.WriteInt32LittleEndian(frame, reply.Length);
                reply.CopyTo(frame, 4);
                await stream.WriteAsync(frame);
            }
        });
        return Task.FromResult((listener, server));
    }

    [Fact]
    public async Task SendsSecretAndCommand_AndReturnsTheGameReply()
    {
        string? seenSecret = null, seenCommand = null;
        var (listener, server) = await StartFakeGame(req =>
        {
            seenSecret = req.GetProperty("auth").GetString();
            seenCommand = req.GetProperty("command").GetString();
            return JsonSerializer.Serialize(new { id = req.GetProperty("id").GetString(), success = true, response = "3 players" });
        });

        var config = new BridgeConfig { Port = ((IPEndPoint)listener.LocalEndpoint).Port, Secret = new string('s', 40), TimeoutSeconds = 5 };
        using var bridge = new TcpGameBridge(config);

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);
        await server;
        listener.Stop();

        Assert.True(result.Success);
        Assert.Equal("3 players", result.Text);
        Assert.Equal(config.Secret, seenSecret);
        Assert.Equal("ListPlayers", seenCommand);
    }

    [Fact]
    public async Task MismatchedReplyId_IsTreatedAsFailure()
    {
        var (listener, server) = await StartFakeGame(_ =>
            JsonSerializer.Serialize(new { id = "999", success = true, response = "forged" }));

        var config = new BridgeConfig { Port = ((IPEndPoint)listener.LocalEndpoint).Port, Secret = new string('s', 40), TimeoutSeconds = 5 };
        using var bridge = new TcpGameBridge(config);

        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);
        await server;
        listener.Stop();

        Assert.False(result.Success);
        Assert.DoesNotContain("forged", result.Text);
    }

    [Fact]
    public async Task ReconnectsByItself_WhenTheGameSideClosedTheConnection()
    {
        // A fake game that serves ONE request per connection, then hangs up (like a game restart).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int connections = 0;
        Task server = Task.Run(async () =>
        {
            for (int n = 0; n < 2; n++)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref connections);
                NetworkStream stream = client.GetStream();
                byte[] header = new byte[4];
                await stream.ReadExactlyAsync(header);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                await stream.ReadExactlyAsync(body);
                using JsonDocument doc = JsonDocument.Parse(body);
                byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new { id = doc.RootElement.GetProperty("id").GetString(), success = true, response = "pong" + n }));
                byte[] frame = new byte[4 + reply.Length];
                BinaryPrimitives.WriteInt32LittleEndian(frame, reply.Length);
                reply.CopyTo(frame, 4);
                await stream.WriteAsync(frame);
            }
        });

        var config = new BridgeConfig { Port = ((IPEndPoint)listener.LocalEndpoint).Port, Secret = new string('s', 40), TimeoutSeconds = 5 };
        using var bridge = new TcpGameBridge(config);

        BridgeResult first = await bridge.ExecuteAsync("a", CancellationToken.None);
        await Task.Delay(300); // let the hang-up arrive
        BridgeResult second = await bridge.ExecuteAsync("b", CancellationToken.None);
        await server;
        listener.Stop();

        Assert.Equal("pong0", first.Text);
        Assert.True(second.Success, second.Text);
        Assert.Equal("pong1", second.Text);
        Assert.Equal(2, connections);
    }

    [Fact]
    public async Task GameNotRunning_GivesAFriendlyFailure_NotAnException()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var bridge = new TcpGameBridge(new BridgeConfig { Port = freePort, Secret = new string('s', 40), TimeoutSeconds = 2 });
        BridgeResult result = await bridge.ExecuteAsync("ListPlayers", CancellationToken.None);

        // Windows may retry a refused loopback connection for a while, so the
        // failure arrives either as "unavailable" or as a timeout. Both are fine.
        Assert.False(result.Success);
        Assert.True(
            result.Text.Contains("unavailable") || result.Text.Contains("did not answer"),
            result.Text);
    }
}
