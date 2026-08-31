using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

/// <summary>
/// 表示登录端点接收的凭据
/// </summary>
public sealed class LoginRequest
{
    /// <summary>
    /// 获取登录用户名
    /// </summary>
    [Required]
    [StringLength(128)]
    public string? Username { get; init; }

    /// <summary>
    /// 获取仅用于本次认证的密码明文
    /// </summary>
    [Required]
    [StringLength(256)]
    public string? Password { get; init; }
}
