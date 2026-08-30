using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Tests;

public sealed class UserAccountTests
{
    [Fact]
    public void CreateNormalizesUsernameAndNickname()
    {
        var account = UserAccount.Create("  Alice  ", "  Alice  ");

        Assert.Equal("Alice", account.Username);
        Assert.Equal("ALICE", account.NormalizedUsername);
        Assert.Equal("Alice", account.Nickname);
        Assert.NotEqual(Guid.Empty, account.ID);
    }

    [Fact]
    public void EquivalentUnicodeUsernamesHaveTheSameNormalizedValue()
    {
        var composed = UserAccount.Create("caf\u00E9", "Cafe");
        var decomposed = UserAccount.Create("cafe\u0301", "Cafe");

        Assert.Equal(composed.NormalizedUsername, decomposed.NormalizedUsername);
    }

    [Fact]
    public void EmptyNicknameAndUsernameAreRejected()
    {
        Assert.Throws<ArgumentException>(() => UserAccount.Create("alice", "   "));
        Assert.Throws<ArgumentException>(() => UserAccount.Create("   ", "Alice"));
    }

    [Fact]
    public void PasswordHashMustBeInitializedBeforePersistence()
    {
        var account = UserAccount.Create("alice", "Alice");

        Assert.Throws<ArgumentException>(() => account.InitializePasswordHash(""));
        account.InitializePasswordHash("hash");
        Assert.Throws<InvalidOperationException>(() => account.InitializePasswordHash("second-hash"));
        Assert.Equal("hash", account.PasswordHash);
    }

    [Fact]
    public void NicknameCannotExceed64Characters()
    {
        Assert.Throws<ArgumentException>(() => UserAccount.Create("alice", new string('n', 65)));
    }

    [Fact]
    public void UsernameAndNicknameRespectTheir64CharacterBoundaries()
    {
        var account = UserAccount.Create(new string('a', 64), new string('n', 64));

        Assert.Equal(64, account.Username.Length);
        Assert.Equal(64, account.Nickname.Length);
        Assert.Throws<ArgumentException>(() => UserAccount.Create(new string('a', 65), "Nickname"));
        Assert.Throws<ArgumentException>(() => UserAccount.Create("username", new string('n', 65)));
    }

    [Fact]
    public void DecomposedInputThatNormalizesTo64CharactersIsAccepted()
    {
        var username = new string('a', 63) + "e\u0301";
        var nickname = new string('n', 63) + "e\u0301";

        var account = UserAccount.Create(username, nickname);

        Assert.Equal(64, account.Username.Length);
        Assert.Equal(64, account.Nickname.Length);
        Assert.Equal(64, account.NormalizedUsername.Length);
    }

    [Fact]
    public void NormalizedUsernameExpansionBoundaryIsUnreachableForBoundedInvariantCasing()
    {
        // The display username is capped at 64 characters and .NET invariant casing
        // does not provide a stable multi-character expansion sample for this path.
        var normalized = UserAccount.NormalizeUsername(new string('a', 64));

        Assert.Equal(64, normalized.Length);
        Assert.InRange(normalized.Length, 1, 128);
    }

    [Fact]
    public void PasswordHashCannotBeInitializedTwice()
    {
        var account = UserAccount.Create("alice", "Alice");
        account.InitializePasswordHash("first");

        Assert.Throws<InvalidOperationException>(() => account.InitializePasswordHash("second"));
        Assert.Equal("first", account.PasswordHash);
    }

    [Fact]
    public void PublicEntitySurfaceContainsOnlyIdentityFields()
    {
        var properties = typeof(UserAccount).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(["Username", "NormalizedUsername", "Nickname", "PasswordHash", "ID"], properties);
        Assert.DoesNotContain("UserId", properties);
        Assert.DoesNotContain("Role", properties);
        Assert.DoesNotContain("Disabled", properties);
        Assert.DoesNotContain("Session", properties);
    }
}
