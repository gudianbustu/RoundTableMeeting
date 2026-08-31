namespace RoundTableMeeting.Authentication;

/// <summary>
/// 集中定义 Cookie 认证方案使用的标识
/// </summary>
public static class AuthenticationConstants
{
    /// <summary>
    /// 应用认证方案名称
    /// </summary>
    public const string Scheme = "RoundTableMeetingCookie";
    /// <summary>
    /// 认证 Cookie 名称
    /// </summary>
    public const string CookieName = "RoundTableMeeting.Auth";
    /// <summary>
    /// 承载用户标识的声明类型
    /// </summary>
    public const string ClaimType = "round_table_meeting_user_id";
}
