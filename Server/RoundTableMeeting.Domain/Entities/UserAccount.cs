using System.Globalization;
using System.Text;

namespace RoundTableMeeting.Domain.Entities;

public sealed class UserAccount : BaseEntity
{
    public string Username { get; private set; } = string.Empty;

    public string NormalizedUsername { get; private set; } = string.Empty;

    public string Nickname { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public UserAccount()
    {
    }

    private UserAccount(string username, string nickname)
    {
        Username = username;
        NormalizedUsername = NormalizeUsername(username);
        Nickname = NormalizeNickname(nickname);
    }

    public static UserAccount Create(string username, string nickname)
    {
        var displayName = NormalizeDisplayUsername(username);
        var normalized = NormalizeUsername(displayName);
        var normalizedNickname = NormalizeNickname(nickname);
        if (normalizedNickname.Length == 0)
        {
            throw new ArgumentException("Nickname is required.", nameof(nickname));
        }

        return new UserAccount(displayName, normalizedNickname)
        {
            NormalizedUsername = normalized
        };
    }

    public void InitializePasswordHash(string passwordHash)
    {
        if (!string.IsNullOrEmpty(PasswordHash))
        {
            throw new InvalidOperationException("Password hash is already initialized.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
    }

    public static string NormalizeDisplayUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Username is required.", nameof(value));
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.Length > 64) throw new ArgumentException("Username length is invalid.", nameof(value));
        return normalized;
    }

    public static string NormalizeUsername(string value)
    {
        var display = NormalizeDisplayUsername(value);
        var normalized = display.ToUpperInvariant();
        if (normalized.Length > 128) throw new ArgumentException("Normalized username length is invalid.", nameof(value));
        return normalized;
    }

    public static string NormalizeNickname(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.Length > 64) throw new ArgumentException("Nickname length is invalid.", nameof(value));
        return normalized;
    }
}
