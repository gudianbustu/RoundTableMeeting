namespace RoundTableMeeting.Abstraction.Services;

/// <summary>
/// 提供当前请求的认证身份信息
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>
    /// 获取当前请求是否具有有效认证身份
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// 获取当前用户标识，未认证或标识无效时返回 <see langword="null"/>
    /// </summary>
    Guid? UserId { get; }
}
