using System.Net;
using System.Net.Sockets;
using ScumRcon.Config;
using ScumRcon.Logging;

namespace ScumRcon.Server.Tests;

/// <summary>A second copy of the server (left running in another window) must give a clear message, not a stack trace.</summary>
public sealed class StartupProblemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumrcon-startup-" + Guid.NewGuid().ToString("N"));

    public StartupProblemTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void AnAuditLogThatIsFree_IsOpened()
    {
        using FileAuditSink? sink = Program.TryOpenAuditLog(Path.Combine(_dir, "logs", "audit.jsonl"), out string? problem);

        Assert.NotNull(sink);
        Assert.Null(problem);
    }

    [Fact]
    public void AnAuditLogHeldByAnotherServer_GivesAClearMessage()
    {
        string path = Path.Combine(_dir, "audit.jsonl");
        using var firstServer = new FileAuditSink(path);          // keeps the file open like a running server

        FileAuditSink? second = Program.TryOpenAuditLog(path, out string? problem);

        Assert.Null(second);
        Assert.NotNull(problem);
        Assert.Contains("audit.jsonl", problem);
        Assert.Contains("already running", problem);
        Assert.DoesNotContain("   at ", problem);                 // no stack trace
    }

    [Fact]
    public void APortThatIsAlreadyInUse_GivesAClearMessage()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        try
        {
            int port = ((IPEndPoint)first.LocalEndpoint).Port;
            var second = new TcpListener(IPAddress.Loopback, port);
            SocketException ex = Assert.Throws<SocketException>(() => second.Start());
            var config = new ServerConfig { ListenAddress = "127.0.0.1", Port = port };

            string text = Program.DescribeListenProblem(config, ex);

            Assert.Contains($"127.0.0.1:{port}", text);
            Assert.Contains("already in use", text);
        }
        finally
        {
            first.Stop();
        }
    }
}
