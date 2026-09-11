namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

/// <summary>
/// 表示提供给客户端的当前用户信息
/// </summary>
public sealed record CurrentUserDto(Guid UserId, string Username, string Nickname, bool IsAdmin);
