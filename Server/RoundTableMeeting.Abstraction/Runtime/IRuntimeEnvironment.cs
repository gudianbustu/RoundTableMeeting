namespace RoundTableMeeting.Abstraction.Runtime;

/// <summary>
/// 独立于宿主框架提供运行环境信息
/// </summary>
public interface IRuntimeEnvironment
{
    /// <summary>
    /// 获取当前环境名称
    /// </summary>
    string EnvironmentName { get; }
}
