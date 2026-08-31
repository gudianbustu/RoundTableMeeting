namespace RoundTableMeeting.Authentication;

/// <summary>
/// 标记响应不得被浏览器或代理缓存的端点。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NoStoreResponseAttribute : Attribute
{
}
