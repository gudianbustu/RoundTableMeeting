using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RoundTableMeeting.Repository.Sqlite.Database;

public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMilliseconds = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetBusyTimeout(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetBusyTimeoutAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

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
