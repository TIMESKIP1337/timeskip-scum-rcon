using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using ScumRcon.Config;

namespace ScumRcon.GameBridge;

/// <summary>Outcome of running a command in the game.</summary>
public readonly record struct BridgeResult(bool Success, string Text);

/// <summary>The in-game side of the system, as the RCON server sees it.</summary>
public interface IGameBridge
{
    Task<BridgeResult> ExecuteAsync(string command, CancellationToken ct);
}

/// <summary>
/// Talks to the game plugin over loopback TCP.
/// Frame: 4-byte little-endian length, then UTF-8 JSON.
/// Request : {"id":"7","auth":"&lt;secret&gt;","command":"ListPlayers"}
/// Response: {"id":"7","success":true,"response":"..."}
/// One request at a time. See docs/BRIDGE-PROTOCOL.md.
/// </summary>
public sealed class TcpGameBridge : IGameBridge, IDisposable
{
    private const int MaxFrameBytes = 64 * 1024;

    private readonly BridgeConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _nextId;

    public TcpGameBridge(BridgeConfig config) => _config = config;

    public async Task<BridgeResult> ExecuteAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
            try
            {
                await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);

                string id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id, auth = _config.Secret, command });
                if (payload.Length > MaxFrameBytes)
                    return new BridgeResult(false, "Command is too large for the game bridge.");

                await WriteFrameAsync(payload, timeout.Token).ConfigureAwait(false);
                byte[] reply = await ReadFrameAsync(timeout.Token).ConfigureAwait(false);

                using JsonDocument doc = JsonDocument.Parse(reply);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("id", out JsonElement replyId) || replyId.GetString() != id)
                    throw new InvalidDataException("Bridge reply does not match the request.");

                bool ok = root.TryGetProperty("success", out JsonElement s) && s.ValueKind == JsonValueKind.True;
                string text = root.TryGetProperty("response", out JsonElement r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? string.Empty
                    : string.Empty;
                return new BridgeResult(ok, text);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Reset();
                return new BridgeResult(false, "The game did not answer in time.");
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException or ObjectDisposedException)
            {
                Reset();
                return new BridgeResult(false, "The game bridge is unavailable.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        // Reuse the connection only if the game side has not closed it (for example after a game
        // restart). Checking BEFORE sending avoids ever running a command twice.
        if (_client is { Connected: true } && _stream is not null && !PeerClosed(_client)) return;
        Reset();

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        client.NoDelay = true;
        _client = client;
        _stream = client.GetStream();
    }

    private static bool PeerClosed(TcpClient client)
    {
        try
        {
            return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    private async Task WriteFrameAsync(byte[] payload, CancellationToken ct)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame, 4);
        await _stream!.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        byte[] header = new byte[4];
        await ReadExactAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException("Bridge reply has an invalid length.");

        byte[] body = new byte[length];
        await ReadExactAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The game bridge closed the connection.");
            total += n;
        }
    }

    private void Reset()
    {
        try { _stream?.Dispose(); } catch (IOException) { }
        try { _client?.Dispose(); } catch (IOException) { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Reset();
        _gate.Dispose();
    }
}
