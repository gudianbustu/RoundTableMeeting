using Microsoft.EntityFrameworkCore;

namespace RoundTableMeeting.Repository.Sqlite.Database;

public sealed class RoundTableMeetingDbContext(DbContextOptions<RoundTableMeetingDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoundTableMeetingDbContext).Assembly);
    }
}
