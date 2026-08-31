namespace RoundTableMeeting.Abstraction.Contracts.Authentication;

/// <summary>
/// 标识一次凭据认证的结果
/// </summary>
public enum AuthenticateStatus { Success, InvalidCredentials, ValidationFailed }

/// <summary>
/// 携带认证状态、认证用户和字段校验错误
/// </summary>
public sealed record AuthenticateResult(AuthenticateStatus Status, CurrentUserDto? User = null, IReadOnlyDictionary<string, string[]>? Errors = null);
