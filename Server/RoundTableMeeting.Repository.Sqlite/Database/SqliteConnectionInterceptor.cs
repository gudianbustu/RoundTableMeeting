using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RoundTableMeeting.Repository.Sqlite.Database;

/// <summary>
/// 为 SQLite 连接应用运行参数
/// </summary>
public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// SQLite 锁等待超时时间
    /// </summary>
    private const int BusyTimeoutMilliseconds = 5000;

    /// <summary>
    /// 为同步打开的 SQLite 连接设置参数
    /// </summary>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetBusyTimeout(connection);
        base.ConnectionOpened(connection, eventData);
    }

    /// <summary>
    /// 为异步打开的 SQLite 连接设置忙等待超时
    /// </summary>
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetBusyTimeoutAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    /// <summary>
    /// 为同步连接设置 SQLite 忙等待超时
    /// </summary>
    private static void SetBusyTimeout(DbConnection connection)
    {
        if (connection is not SqliteConnection sqliteConnection)
        {
            return;
        }

        using var command = sqliteConnection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 为异步连接设置 SQLite 忙等待超时
    /// </summary>
    private static async Task SetBusyTimeoutAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection sqliteConnection)
        {
            return;
        }

        await using var command = sqliteConnection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
