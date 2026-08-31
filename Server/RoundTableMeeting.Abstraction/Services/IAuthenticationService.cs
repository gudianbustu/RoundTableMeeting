using RoundTableMeeting.Abstraction.Contracts.Authentication;

namespace RoundTableMeeting.Abstraction.Services;

/// <summary>
/// 定义账号认证和当前用户查询用例
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// 校验登录输入并验证账号凭据
    /// </summary>
    Task<AuthenticateResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析当前请求身份所代表的用户
    /// </summary>
    Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
