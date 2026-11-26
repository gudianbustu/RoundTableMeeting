using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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

    /// <summary>由服务端提供，仅在账号创建流程中传递。</summary>
    [JsonIgnore]
    public string? InitialPassword { get; init; }
}
