namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

/// <summary>
/// 表示当前请求是否具有有效用户身份
/// </summary>
public enum CurrentUserStatus { Success, Unauthenticated }

/// <summary>
/// 携带当前用户查询状态及对应的用户信息
/// </summary>
public sealed record CurrentUserResult(CurrentUserStatus Status, CurrentUserDto? User = null);
