using Microsoft.Data.Sqlite;

namespace RoundTableMeeting.Repository.Sqlite.Configuration;

/// <summary>
/// 解析 SQLite 数据源路径
/// </summary>
public static class SqlitePathResolver
{
    /// <summary>
    /// 规范化 SQLite 数据源路径
    /// </summary>
    public static string NormalizeConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException("SQLite DataSource must be configured.");
        }

        if (builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return builder.ToString();
        }

        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.DataSource));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 获取 SQLite 数据库所在目录
    /// </summary>
    public static string GetDatabaseDirectory(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return AppContext.BaseDirectory;
        }

        return Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? AppContext.BaseDirectory;
    }
}
