using System.ComponentModel.DataAnnotations;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Authentication;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    [MinLength(1)]
    public List<string> AllowedOrigins { get; set; } = [];

    [MinLength(1)]
    public List<SeedAccountOptions> SeedAccounts { get; set; } = [];

    public bool Validate(out string error)
    {
        error = string.Empty;
        if (AllowedOrigins.Count == 0 || AllowedOrigins.Any(origin => !IsPureOrigin(origin)))
        {
            error = "Authentication:AllowedOrigins must contain absolute browser origins.";
            return false;
        }

        if (AllowedOrigins.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedOrigins.Count)
        {
            error = "Authentication:AllowedOrigins must not contain duplicates.";
            return false;
        }

        if (SeedAccounts.Count == 0 || SeedAccounts.Any(account => string.IsNullOrEmpty(account.Password) || account.Password.Length > 256))
        {
            error = "Authentication:SeedAccounts must contain valid accounts.";
            return false;
        }

        try
        {
            var normalized = SeedAccounts.Select(account =>
            {
                var username = UserAccount.NormalizeDisplayUsername(account.Username ?? string.Empty);
                _ = UserAccount.NormalizeNickname(account.Nickname ?? string.Empty);
                return UserAccount.NormalizeUsername(username);
            }).ToArray();
            if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length) error = "Authentication:SeedAccounts usernames must be unique.";
        }
        catch (ArgumentException)
        {
            error = "Authentication:SeedAccounts contains invalid account values.";
        }
        return error.Length == 0;
    }

    private static bool IsPureOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/"
            || !string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

public sealed class SeedAccountOptions
{
    [Required]
    public string? Username { get; set; }

    [Required]
    public string? Nickname { get; set; }

    [Required]
    public string? Password { get; set; }
}
