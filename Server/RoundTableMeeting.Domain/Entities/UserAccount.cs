using System.Globalization;
using System.Text;

namespace RoundTableMeeting.Domain.Entities;

/// <summary>
/// 表示可用于登录的用户账号
/// </summary>
public sealed class UserAccount : BaseEntity
{
    /// <summary>
    /// 账号用户名
    /// </summary>
    public string Username { get; private set; } = string.Empty;

    /// <summary>
    /// 用于唯一查询的规范化用户名
    /// </summary>
    public string NormalizedUsername { get; private set; } = string.Empty;

    /// <summary>
    /// 账号昵称
    /// </summary>
    public string Nickname { get; private set; } = string.Empty;

    /// <summary>
    /// 账号密码哈希
    /// </summary>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>
    /// 创建供 EF Core 物化的空账号实例
    /// </summary>
    public UserAccount()
    {
    }

    /// <summary>
    /// 使用已规范化的领域值初始化账号
    /// </summary>
    private UserAccount(string username, string nickname)
    {
        Username = username;
        NormalizedUsername = NormalizeUsername(username);
        Nickname = NormalizeNickname(nickname);
    }

    /// <summary>
    /// 校验输入并创建用户账号
    /// </summary>
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

    /// <summary>
    /// 初始化账号密码哈希并防止重复设置
    /// </summary>
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

    /// <summary>
    /// 修改账号昵称。
    /// </summary>
    public void ChangeNickname(string nickname)
    {
        Nickname = NormalizeNickname(nickname);
    }

    /// <summary>
    /// 替换已有账号的密码哈希。
    /// </summary>
    public void ReplacePasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash) || passwordHash.Length > 512)
        {
            throw new ArgumentException("Password hash is invalid.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
    }

    /// <summary>
    /// 清理并校验用于显示的用户名
    /// </summary>
    public static string NormalizeDisplayUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Username is required.", nameof(value));
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.Length > 64) throw new ArgumentException("Username length is invalid.", nameof(value));
        return normalized;
    }

    /// <summary>
    /// 将用户名转换为大小写不敏感的唯一比较形式
    /// </summary>
    public static string NormalizeUsername(string value)
    {
        var display = NormalizeDisplayUsername(value);
        var normalized = display.ToUpperInvariant();
        if (normalized.Length > 128) throw new ArgumentException("Normalized username length is invalid.", nameof(value));
        return normalized;
    }

    /// <summary>
    /// 清理并校验用户昵称
    /// </summary>
    public static string NormalizeNickname(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 || normalized.Length > 64) throw new ArgumentException("Nickname length is invalid.", nameof(value));
        return normalized;
    }
}
