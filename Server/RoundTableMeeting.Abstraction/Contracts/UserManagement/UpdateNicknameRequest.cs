using System.ComponentModel.DataAnnotations;

namespace RoundTableMeeting.Abstraction.Contracts.UserManagement;

/// <summary>
/// 修改用户昵称的输入。
/// </summary>
public sealed class UpdateNicknameRequest
{
    [Required, StringLength(64)]
    public string? Nickname { get; init; }
}
