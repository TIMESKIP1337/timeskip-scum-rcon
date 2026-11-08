using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ScumRcon.Config;
using ScumRcon.GameBridge;
using ScumRcon.Logging;
using ScumRcon.Protocol;
using ScumRcon.Security;

namespace ScumRcon.Core;

/// <summary>
/// The RCON server. Per connection, in order:
///   address allowlist -> blocked-client check -> connection limit ->
///   login ("user:password", with a time limit) -> command loop
///   (role check, command policy, rate limit, then the game bridge).
/// Every decision is written to the audit sink. Passwords never are.
/// </summary>
public sealed class RconServer : IAsyncDisposable
{
    private sealed record Account(string Hash, Role Role);

    private readonly ServerConfig _config;
    private readonly IGameBridge _bridge;
    private readonly IAuditSink _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IpAllowlist _allowlist;
    private readonly CommandPolicy _policy;
    private readonly AuthThrottle _throttle;
    private readonly Dictionary<string, Account> _accounts;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public RconServer(ServerConfig config, IGameBridge bridge, IAuditSink audit, Func<DateTimeOffset>? clock = null)
    {
        _config = config;
        _bridge = bridge;
        _audit = audit;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        _allowlist = new IpAllowlist(config.IpAllowlist);
        _slots = new SemaphoreSlim(config.MaxConnections, config.MaxConnections);
        _throttle = new AuthThrottle(
            config.MaxFailedAuth,
            TimeSpan.FromMinutes(config.FailureWindowMinutes),
            TimeSpan.FromMinutes(config.BlockMinutes),
            _clock);

        var roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.CommandRoles)
        {
            if (ConfigLoader.TryParseRole(pair.Value, out Role role)) roles[pair.Key] = role;
        }
        _policy = new CommandPolicy(roles, config.MaxCommandLength);

        _accounts = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
        foreach (UserConfig u in config.Users)
        {
            if (ConfigLoader.TryParseRole(u.Role, out Role role))
                _accounts[u.Name] = new Account(u.PasswordHash, role);
        }
    }

    /// <summary>The address and port actually in use (useful when Port is 0).</summary>
    public IPEndPoint LocalEndpoint =>
        (IPEndPoint?)_listener?.LocalEndpoint ?? throw new InvalidOperationException("Server is not running.");

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("Server already started.");
        var address = IPAddress.Parse(_config.ListenAddress);
        _listener = new TcpListener(address, _config.Port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await Task.WhenAll(_sessions.Keys).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _slots.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }

            Task session = RunSessionAsync(client, ct);
            _sessions.TryAdd(session, 0);
            _ = session.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken ct)
    {
        string ip = "unknown";
        bool slotTaken = false;
        try
        {
            using (client)
            {
                IPAddress? address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (address is null) return;
                ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

                if (!_allowlist.IsAllowed(address))
                {
                    Log("rejected_ip", ip);
                    return;
                }
                if (_throttle.IsBlocked(ip, out TimeSpan remaining))
                {
                    Log("blocked", ip, ("remainingSeconds", (int)remaining.TotalSeconds));
                    return;
                }
                if (!_slots.Wait(0))
                {
                    Log("too_many_connections", ip);
                    return;
                }
                slotTaken = true;

                client.NoDelay = true;
                try
                {
                    await HandleAsync(client.GetStream(), ip, ct).ConfigureAwait(false);
                }
                catch (RconProtocolException ex)
                {
                    // Logged BEFORE the socket is closed, so the record exists by the time the client notices.
                    Log("protocol_error", ip, ("reason", ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // client vanished; nothing to do
        }
        catch (Exception ex)
        {
            Log("session_error", ip, ("type", ex.GetType().Name));
        }
        finally
        {
            if (slotTaken) _slots.Release();
        }
    }

    private async Task HandleAsync(Stream stream, string ip, CancellationToken ct)
    {
        // ---- login -------------------------------------------------------
        RconPacket? first;
        using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            authTimeout.CancelAfter(TimeSpan.FromSeconds(_config.AuthTimeoutSeconds));
            try
            {
                first = await RconPacket.ReadAsync(stream, authTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log("auth_timeout", ip);
                return;
            }
        }
        if (first is null || first.Value.Type != PacketType.Auth) return;

        RconPacket login = first.Value;
        (string? user, string password) = SplitCredentials(login.Body);

        Account? account = null;
        if (user is not null) _accounts.TryGetValue(user, out account);

        // Always run one hash check, even for unknown users, so timing does not reveal valid names.
        bool passwordOk = PasswordHasher.Verify(password, account?.Hash ?? PasswordHasher.DummyHash);
        if (account is null || !passwordOk)
        {
            bool blockedNow = _throttle.RegisterFailure(ip);
            Log("auth_fail", ip, ("user", Truncate(user ?? string.Empty, 32)), ("blockedNow", blockedNow));
            await SendAsync(stream, new RconPacket(-1, PacketType.AuthResponse, string.Empty), ct).ConfigureAwait(false);
            return;
        }

        _throttle.RegisterSuccess(ip);
        Log("auth_ok", ip, ("user", user), ("role", account.Role.ToString()));
        await SendAsync(stream, new RconPacket(login.Id, PacketType.ResponseValue, string.Empty), ct).ConfigureAwait(false);
        await SendAsync(stream, new RconPacket(login.Id, PacketType.AuthResponse, string.Empty), ct).ConfigureAwait(false);

        // ---- commands ----------------------------------------------------
        var limiter = new SlidingWindowLimiter(_config.MaxCommandsPerSecond, TimeSpan.FromSeconds(1), _clock);
        while (!ct.IsCancellationRequested)
        {
            RconPacket? packet;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(_config.IdleTimeoutSeconds));
                try
                {
                    packet = await RconPacket.ReadAsync(stream, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log("idle_timeout", ip, ("user", user));
                    return;
                }
            }
            if (packet is null) return;
            if (packet.Value.Type != PacketType.ExecCommand)
                throw new RconProtocolException($"Unexpected packet type {packet.Value.Type} after login.");

            RconPacket request = packet.Value;
            string reply;

            if (!limiter.TryAcquire())
            {
                Log("rate_limited", ip, ("user", user));
                reply = "Too many commands. Slow down.";
            }
            else
            {
                PolicyDecision decision = _policy.Check(account.Role, request.Body);
                if (!decision.Allowed)
                {
                    Log("cmd_denied", ip, ("user", user), ("role", account.Role.ToString()),
                        ("command", Truncate(request.Body, 256)), ("reason", decision.Reason));
                    reply = "Denied: " + decision.Reason;
                }
                else
                {
                    BridgeResult result = await _bridge.ExecuteAsync(request.Body, ct).ConfigureAwait(false);
                    Log("cmd", ip, ("user", user), ("role", account.Role.ToString()),
                        ("command", Truncate(request.Body, 256)), ("success", result.Success));
                    reply = result.Success ? result.Text : "Error: " + result.Text;
                }
            }

            foreach (string piece in ResponseSplitter.Split(reply))
                await SendAsync(stream, new RconPacket(request.Id, PacketType.ResponseValue, piece), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Login body is "user:password". The password may itself contain ':'.</summary>
    private static (string? User, string Password) SplitCredentials(string body)
    {
        int colon = body.IndexOf(':');
        if (colon <= 0) return (null, body);
        return (body[..colon], body[(colon + 1)..]);
    }

    private static async Task SendAsync(Stream stream, RconPacket packet, CancellationToken ct)
    {
        await stream.WriteAsync(packet.Serialize(), ct).ConfigureAwait(false);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private void Log(string eventName, string ip, params (string Key, object? Value)[] fields)
    {
        var data = new Dictionary<string, object?> { ["ip"] = ip };
        foreach (var (key, value) in fields) data[key] = value;
        _audit.Write(eventName, data);
    }
}
