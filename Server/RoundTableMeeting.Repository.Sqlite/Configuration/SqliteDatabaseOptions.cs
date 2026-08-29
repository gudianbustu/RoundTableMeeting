using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;

namespace RoundTableMeeting.Repository.Sqlite.Configuration;

public sealed class SqliteDatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    public static bool TryValidate(string? connectionString, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error = "Database connection string is required.";
            return false;
        }

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            error = "Database connection string is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            error = "SQLite DataSource is required.";
            return false;
        }

        if (builder.Mode == SqliteOpenMode.Memory
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            error = "An in-memory SQLite database is not allowed.";
            return false;
        }

        if (builder.DefaultTimeout <= 0)
        {
            error = "SQLite Default Timeout must be greater than zero.";
            return false;
        }

        return true;
    }
}
