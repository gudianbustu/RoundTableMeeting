using System.ComponentModel.DataAnnotations;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Authentication;

/// <summary>
/// 保存浏览器来源和启动账号配置
/// </summary>
public sealed class AuthenticationOptions
{
    /// <summary>
    /// 仅当尚无管理员时使用的显式初始管理员用户名
    /// </summary>
    public string? InitialAdminUsername { get; set; }

    /// <summary>管理员创建普通账号时使用的实例初始密码。</summary>
    public string? NewAccountInitialPassword { get; set; }

    public const string SectionName = "Authentication";

    /// <summary>
    /// 允许访问浏览器端点的来源列表
    /// </summary>
    [MinLength(1)]
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// 应用启动时需要创建的账号列表
    /// </summary>
    [MinLength(1)]
    public List<SeedAccountOptions> SeedAccounts { get; set; } = [];

    /// <summary>
    /// 校验认证配置或登录输入
    /// </summary>
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

    /// <summary>
    /// 校验来源是否为纯绝对来源
    /// </summary>
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

/// <summary>
/// 保存一个启动播种账号的配置
/// </summary>
public sealed class SeedAccountOptions
{
    /// <summary>
    /// 账号用户名
    /// </summary>
    [Required]
    public string? Username { get; set; }

    /// <summary>
    /// 账号昵称
    /// </summary>
    [Required]
    public string? Nickname { get; set; }

    /// <summary>
    /// 账号密码
    /// </summary>
    [Required]
    public string? Password { get; set; }
}
