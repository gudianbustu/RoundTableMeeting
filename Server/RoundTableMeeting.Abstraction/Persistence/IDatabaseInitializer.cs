namespace RoundTableMeeting.Abstraction.Persistence;

/// <summary>
/// 定义数据库启动初始化能力
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// 创建数据库目录、应用迁移并校验运行设置
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
