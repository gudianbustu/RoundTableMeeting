using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Abstraction.Contracts.UserManagement;

/// <summary>
/// 创建测试账号的输入。
/// </summary>
public sealed class CreateUserRequest
{
    [Required, StringLength(128)]
    public string? Username { get; init; }

    [Required, StringLength(64)]
    public string? Nickname { get; init; }

    [Required, StringLength(256)]
    public string? InitialPassword { get; init; }
}
