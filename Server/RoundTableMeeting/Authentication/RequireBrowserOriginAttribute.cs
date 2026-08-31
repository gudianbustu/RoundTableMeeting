namespace RoundTableMeeting.Authentication;

/// <summary>
/// 标记需要浏览器来源校验的端点
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireBrowserOriginAttribute(bool requireJsonBody = false) : Attribute
{
    /// <summary>
    /// 端点是否必须接收 application/json 请求体
    /// </summary>
    public bool RequireJsonBody { get; } = requireJsonBody;
}
