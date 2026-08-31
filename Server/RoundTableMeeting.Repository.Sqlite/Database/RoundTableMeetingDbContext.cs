using Microsoft.EntityFrameworkCore;

namespace RoundTableMeeting.Repository.Sqlite.Database;

/// <summary>
/// 表示应用持久化模型的 EF Core 上下文
/// </summary>
public sealed class RoundTableMeetingDbContext(DbContextOptions<RoundTableMeetingDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// 用户账号实体集合
    /// </summary>
    public DbSet<RoundTableMeeting.Domain.Entities.UserAccount> UserAccounts => Set<RoundTableMeeting.Domain.Entities.UserAccount>();
    public DbSet<RoundTableMeeting.Domain.Entities.Meeting> Meetings => Set<RoundTableMeeting.Domain.Entities.Meeting>();
    public DbSet<RoundTableMeeting.Domain.Entities.MeetingParticipant> MeetingParticipants => Set<RoundTableMeeting.Domain.Entities.MeetingParticipant>();
    public DbSet<RoundTableMeeting.Domain.Entities.MeetingCreationReceipt> MeetingCreationReceipts => Set<RoundTableMeeting.Domain.Entities.MeetingCreationReceipt>();

    /// <summary>
    /// 校验账号后同步保存实体变更
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateUserAccounts();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// 校验账号后同步保存实体变更
    /// </summary>
    public override int SaveChanges() => SaveChanges(true);

    /// <summary>
    /// 校验账号后异步保存实体变更
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateUserAccounts();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 校验账号后异步保存实体变更
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(true, cancellationToken);

    /// <summary>
    /// 校验待持久化账号的完整性
    /// </summary>
    private void ValidateUserAccounts()
    {
        // 在 EF 将已跟踪写入发送到 SQLite 前执行校验
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

    /// <summary>
    /// 加载仓储程序集中的实体配置
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoundTableMeetingDbContext).Assembly);
    }
}
