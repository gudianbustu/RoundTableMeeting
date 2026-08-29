using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RoundTableMeeting.Repository.Sqlite.Configuration;

namespace RoundTableMeeting.Repository.Sqlite.Database;

public sealed class RoundTableMeetingDbContextFactory : IDesignTimeDbContextFactory<RoundTableMeetingDbContext>
{
    public RoundTableMeetingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ROUND_TABLE_MEETING_DESIGN_TIME_CONNECTION")
            ?? "Data Source=design-time.db";

        var optionsBuilder = new DbContextOptionsBuilder<RoundTableMeetingDbContext>();
        optionsBuilder.UseSqlite(
            SqlitePathResolver.NormalizeConnectionString(connectionString),
            sqlite => sqlite.MigrationsAssembly(typeof(RoundTableMeetingDbContext).Assembly.FullName));
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.AddInterceptors(new SqliteConnectionInterceptor());
        return new RoundTableMeetingDbContext(optionsBuilder.Options);
    }
}
