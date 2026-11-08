using ScumRcon.Security;

namespace ScumRcon.Server.Tests;

public class ProgramTests
{
    [Theory]
    [InlineData("secret", "secret")]
    [InlineData("﻿secret", "secret")]           // invisible BOM added by a shell pipe
    [InlineData("secret\r", "secret")]
    [InlineData("secret\r\n", "secret")]
    [InlineData("﻿secret\r\n", "secret")]
    [InlineData("  keep inner  spaces  ", "  keep inner  spaces  ")]   // spaces belong to the password
    [InlineData("", "")]
    [InlineData(null, "")]
    public void PipedPassword_IsCleanedWithoutTouchingTheRealCharacters(string? raw, string expected)
    {
        Assert.Equal(expected, Program.CleanPastedPassword(raw));
    }

    [Fact]
    public void HashMadeFromAPipedPassword_MatchesWhatTheAdminTypes()
    {
        string hash = PasswordHasher.Hash(Program.CleanPastedPassword("﻿example-password-not-real\r\n"), 1000);
        Assert.True(PasswordHasher.Verify("example-password-not-real", hash));
    }
}
