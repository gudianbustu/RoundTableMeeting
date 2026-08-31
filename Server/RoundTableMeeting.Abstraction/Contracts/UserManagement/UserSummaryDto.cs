namespace RoundTableMeeting.Abstraction.Contracts.UserManagement;

/// <summary>
/// 提供给客户端的最小用户摘要。
/// </summary>
public sealed record UserSummaryDto(Guid UserId, string Username, string Nickname);
