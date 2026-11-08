using System.Net;
using ScumRcon.Config;
using ScumRcon.Security;

namespace ScumRcon.Server.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void CorrectPassword_Verifies_WrongPassword_DoesNot()
    {
        string stored = PasswordHasher.Hash("correct horse", 1000);
        Assert.True(PasswordHasher.Verify("correct horse", stored));
        Assert.False(PasswordHasher.Verify("correct horsE", stored));
        Assert.False(PasswordHasher.Verify(string.Empty, stored));
    }

    [Fact]
    public void SameStringTwice_GivesDifferentHashes()
    {
        Assert.NotEqual(PasswordHasher.Hash("pw", 1000), PasswordHasher.Hash("pw", 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2-sha256$abc$AAAA$BBBB")]
    [InlineData("pbkdf2-sha256$0$AAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("md5$1000$AAAA$BBBB")]
    public void MalformedStoredValue_NeverVerifies_AndDoesNotThrow(string? stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
        Assert.False(PasswordHasher.IsValidFormat(stored));
    }

    [Fact]
    public void NeedsRehash_WhenIterationsAreLow()
    {
        Assert.True(PasswordHasher.NeedsRehash(PasswordHasher.Hash("x", 1000)));
    }
}

public class IpAllowlistTests
{
    [Fact]
    public void SingleAddresses_AndCidr_Match()
    {
        var list = new IpAllowlist(new[] { "203.0.113.5", "10.0.0.0/8", "192.168.1.128/25", "::1" });

        Assert.True(list.IsAllowed(IPAddress.Parse("203.0.113.5")));
        Assert.False(list.IsAllowed(IPAddress.Parse("203.0.113.6")));
        Assert.True(list.IsAllowed(IPAddress.Parse("10.200.3.4")));
        Assert.False(list.IsAllowed(IPAddress.Parse("11.0.0.1")));
        Assert.True(list.IsAllowed(IPAddress.Parse("192.168.1.200")));
        Assert.False(list.IsAllowed(IPAddress.Parse("192.168.1.100")));
        Assert.True(list.IsAllowed(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Ipv4MappedAddress_IsTreatedAsIpv4()
    {
        var list = new IpAllowlist(new[] { "127.0.0.1" });
        Assert.True(list.IsAllowed(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Fact]
    public void EmptyList_AllowsNobody()
    {
        var list = new IpAllowlist(Array.Empty<string>());
        Assert.False(list.IsAllowed(IPAddress.Loopback));
        Assert.False(list.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("")]
    public void BadEntries_Throw(string entry)
    {
        Assert.Throws<FormatException>(() => new IpAllowlist(new[] { entry }));
    }
}

public class ThrottleTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    [Fact]
    public void Blocks_AfterMaxFailures_ThenUnblocks()
    {
        var clock = new FakeClock();
        var throttle = new AuthThrottle(3, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), clock.Read);

        Assert.False(throttle.RegisterFailure("1.2.3.4"));
        Assert.False(throttle.RegisterFailure("1.2.3.4"));
        Assert.False(throttle.IsBlocked("1.2.3.4", out _));
        Assert.True(throttle.RegisterFailure("1.2.3.4"));
        Assert.True(throttle.IsBlocked("1.2.3.4", out TimeSpan remaining));
        Assert.True(remaining > TimeSpan.FromMinutes(14));

        clock.Now += TimeSpan.FromMinutes(16);
        Assert.False(throttle.IsBlocked("1.2.3.4", out _));
    }

    [Fact]
    public void OldFailures_ExpireOutOfTheWindow()
    {
        var clock = new FakeClock();
        var throttle = new AuthThrottle(2, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), clock.Read);

        throttle.RegisterFailure("a");
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.False(throttle.RegisterFailure("a")); // the first one no longer counts
        Assert.False(throttle.IsBlocked("a", out _));
    }

    [Fact]
    public void Success_ClearsFailures_AndKeysAreIndependent()
    {
        var clock = new FakeClock();
        var throttle = new AuthThrottle(2, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), clock.Read);

        throttle.RegisterFailure("a");
        throttle.RegisterSuccess("a");
        Assert.False(throttle.RegisterFailure("a"));
        Assert.False(throttle.IsBlocked("b", out _));
    }

    [Fact]
    public void SlidingWindow_LimitsThenRecovers()
    {
        var clock = new FakeClock();
        var limiter = new SlidingWindowLimiter(2, TimeSpan.FromSeconds(1), clock.Read);

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
        clock.Now += TimeSpan.FromSeconds(1.1);
        Assert.True(limiter.TryAcquire());
    }
}

public class CommandPolicyTests
{
    private static CommandPolicy Policy() => new(new Dictionary<string, Role>
    {
        ["ListPlayers"] = Role.Viewer,
        ["announce"] = Role.Moderator
    });

    [Fact]
    public void RolesAreOrdered()
    {
        CommandPolicy p = Policy();
        Assert.True(p.Check(Role.Viewer, "ListPlayers").Allowed);
        Assert.False(p.Check(Role.Viewer, "announce hi").Allowed);
        Assert.True(p.Check(Role.Moderator, "#Announce hi").Allowed);
        Assert.True(p.Check(Role.Admin, "listplayers").Allowed);
    }

    [Fact]
    public void UnknownCommand_NeedsAdmin()
    {
        CommandPolicy p = Policy();
        Assert.False(p.Check(Role.Moderator, "SpawnItem Gold 1").Allowed);
        Assert.True(p.Check(Role.Admin, "SpawnItem Gold 1").Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("ListPlayers\nSpawnItem Gold 1")]
    [InlineData("ListPlayers\r")]
    [InlineData("ListPlayers\0")]
    [InlineData("ListPlayers\t\u0007")]
    public void EmptyOrControlCharacters_AreDenied_EvenForAdmin(string command)
    {
        Assert.False(Policy().Check(Role.Admin, command).Allowed);
    }

    [Fact]
    public void TooLongCommand_IsDenied()
    {
        var p = new CommandPolicy(new Dictionary<string, Role> { ["x"] = Role.Viewer }, 10);
        Assert.False(p.Check(Role.Admin, "x " + new string('a', 20)).Allowed);
        Assert.True(p.Check(Role.Admin, "x aaa").Allowed);
    }

    [Fact]
    public void CommandWord_StripsHashAndLowercases()
    {
        Assert.Equal("listplayers", CommandPolicy.CommandWord("  #ListPlayers  extra "));
    }
}

public class ConfigTests
{
    [Fact]
    public void FirstRunConfig_PassesValidation_AndStoresOnlyAHash()
    {
        ServerConfig config = ConfigLoader.CreateFirstRun(out string password);

        Assert.Empty(ConfigLoader.Validate(config));
        Assert.True(password.Length >= 20);
        Assert.DoesNotContain(password, config.Users[0].PasswordHash);
        Assert.True(PasswordHasher.Verify(password, config.Users[0].PasswordHash));
        Assert.Equal(64, config.Bridge.Secret.Length);
    }

    [Fact]
    public void FirstRunConfigs_AreDifferentEachTime()
    {
        ServerConfig a = ConfigLoader.CreateFirstRun(out string pa);
        ServerConfig b = ConfigLoader.CreateFirstRun(out string pb);
        Assert.NotEqual(pa, pb);
        Assert.NotEqual(a.Bridge.Secret, b.Bridge.Secret);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), "scumrcon-" + Guid.NewGuid().ToString("N"), "rcon.json");
        try
        {
            ServerConfig original = ConfigLoader.CreateFirstRun(out _);
            ConfigLoader.Save(original, path);
            ServerConfig loaded = ConfigLoader.Load(path);

            Assert.Equal(original.Bridge.Secret, loaded.Bridge.Secret);
            Assert.Equal(original.Users[0].PasswordHash, loaded.Users[0].PasswordHash);
            Assert.Empty(ConfigLoader.Validate(loaded));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    private static ServerConfig Good()
    {
        ServerConfig c = ConfigLoader.CreateFirstRun(out _);
        return c;
    }

    [Fact]
    public void PlaceholderOrShortSecret_IsRejected()
    {
        ServerConfig c = Good();
        c.Bridge.Secret = "ChangeMe-ChangeMe-ChangeMe-ChangeMe-ChangeMe";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("placeholder"));

        c.Bridge.Secret = "short";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("at least 32"));
    }

    [Fact]
    public void PlainTextPassword_InsteadOfHash_IsRejected()
    {
        ServerConfig c = Good();
        c.Users[0].PasswordHash = "ChangeMe123";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("passwordHash"));
    }

    [Fact]
    public void RemoteBridge_IsRejected()
    {
        ServerConfig c = Good();
        c.Bridge.Host = "203.0.113.9";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("loopback"));
    }

    [Fact]
    public void PublicListen_NeedsAllowlist_AndNeverWholeInternet()
    {
        ServerConfig c = Good();
        c.ListenAddress = "0.0.0.0";
        c.IpAllowlist = new List<string>();
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("non-empty ipAllowlist"));

        c.IpAllowlist = new List<string> { "0.0.0.0/0" };
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("whole internet"));

        c.IpAllowlist = new List<string> { "203.0.113.5" };
        Assert.Empty(ConfigLoader.Validate(c));
    }

    [Fact]
    public void DuplicateUsersBadNamesAndBadRoles_AreRejected()
    {
        ServerConfig c = Good();
        string hash = PasswordHasher.Hash("x", 1000);
        c.Users.Add(new UserConfig { Name = "ADMIN", PasswordHash = hash, Role = "Admin" });
        c.Users.Add(new UserConfig { Name = "bad name!", PasswordHash = hash, Role = "Admin" });
        c.Users.Add(new UserConfig { Name = "carol", PasswordHash = hash, Role = "Root" });

        IReadOnlyList<string> errors = ConfigLoader.Validate(c);
        Assert.Contains(errors, e => e.Contains("listed twice"));
        Assert.Contains(errors, e => e.Contains("1-32 letters"));
        Assert.Contains(errors, e => e.Contains("unknown role 'Root'"));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("Superuser")]
    [InlineData("")]
    public void ParseRole_RefusesNumbersAndUnknownWords(string text)
    {
        Assert.False(ConfigLoader.TryParseRole(text, out _));
    }
}
