using RoundTableMeeting.Abstraction.Runtime;

namespace RoundTableMeeting;

/// <summary>
/// 提供宿主运行环境名称的适配器
/// </summary>
public sealed class RuntimeEnvironment(string environmentName) : IRuntimeEnvironment
{
    /// <summary>
    /// 当前宿主环境名称
    /// </summary>
    public string EnvironmentName { get; } = environmentName;
}
