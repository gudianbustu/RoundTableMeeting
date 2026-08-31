using RoundTableMeeting.Abstraction.Contracts;

namespace RoundTableMeeting.Abstraction.Services;

/// <summary>
/// 定义系统状态查询用例
/// </summary>
public interface ISystemStatusService
{
    /// <summary>
    /// 获取当前服务、数据库和运行环境状态
    /// </summary>
    Task<SystemStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
}
