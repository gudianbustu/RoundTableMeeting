using Microsoft.EntityFrameworkCore;

namespace RoundTableMeeting.Repository.Sqlite.Database;

public sealed class RoundTableMeetingDbContext(DbContextOptions<RoundTableMeetingDbContext> options)
    : DbContext(options)
{
    public DbSet<RoundTableMeeting.Domain.Entities.UserAccount> UserAccounts => Set<RoundTableMeeting.Domain.Entities.UserAccount>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateUserAccounts();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override int SaveChanges() => SaveChanges(true);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateUserAccounts();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(true, cancellationToken);

    private void ValidateUserAccounts()
    {
        foreach (var entry in ChangeTracker.Entries<RoundTableMeeting.Domain.Entities.UserAccount>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var account = entry.Entity;
            if (string.IsNullOrEmpty(account.Username)
                || account.Username.Length > 64
                || string.IsNullOrEmpty(account.NormalizedUsername)
                || account.NormalizedUsername.Length > 128
                || string.IsNullOrEmpty(account.Nickname)
                || account.Nickname.Length > 64
                || string.IsNullOrEmpty(account.PasswordHash))
            {
                throw new InvalidOperationException("User account is not initialized for persistence.");
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoundTableMeetingDbContext).Assembly);
    }
}
