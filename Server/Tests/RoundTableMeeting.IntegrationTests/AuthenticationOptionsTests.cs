using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AuthenticationOptionsTests
{
    [Fact]
    public void InvalidNicknameIsRejectedWithoutEchoingTheConfiguredValue()
    {
        var nickname = new string('n', 65);
        var options = new AuthenticationOptions
        {
            AllowedOrigins = ["http://localhost:5173"],
            SeedAccounts = [new() { Username = "alice", Nickname = nickname, Password = "secret" }]
        };

        Assert.False(options.Validate(out var error));
        Assert.DoesNotContain(nickname, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidOriginWithPortAndNormalizedAccountIsAccepted()
    {
        var options = new AuthenticationOptions
        {
            AllowedOrigins = ["https://localhost:7043"],
            SeedAccounts = [new() { Username = " alice ", Nickname = " Alice ", Password = "secret" }]
        };

        Assert.True(options.Validate(out var error), error);
    }
}
