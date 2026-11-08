using System.Text.Json;

namespace ScumRcon.Logging;

/// <summary>Receives security-relevant events (logins, blocked clients, commands).</summary>
public interface IAuditSink
{
    void Write(string eventName, IReadOnlyDictionary<string, object?> fields);
}

/// <summary>Discards everything.</summary>
public sealed class NullAuditSink : IAuditSink
{
    public void Write(string eventName, IReadOnlyDictionary<string, object?> fields) { }
}

/// <summary>
/// Appends one JSON object per line. Passwords are never passed to this class.
/// Newlines inside values are escaped by JSON, so one event is always one line.
/// </summary>
public sealed class FileAuditSink : IAuditSink, IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public FileAuditSink(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _writer = new StreamWriter(new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Write(string eventName, IReadOnlyDictionary<string, object?> fields)
    {
        var record = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.UtcNow.ToString("o"),
            ["event"] = eventName
        };
        foreach (var pair in fields) record[pair.Key] = pair.Value;

        string line = JsonSerializer.Serialize(record);
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
