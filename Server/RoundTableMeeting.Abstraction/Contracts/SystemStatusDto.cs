namespace RoundTableMeeting.Abstraction.Contracts;

/// <summary>
/// 表示服务、数据库和运行环境的综合状态
/// </summary>
public sealed record SystemStatusDto(string Status, DatabaseStatusDto Database, string Environment);
