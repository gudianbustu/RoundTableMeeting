using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

/// <summary>
/// 配置用户账号的 SQLite 映射
/// </summary>
public sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    /// <summary>
    /// 配置实体表、字段约束和唯一索引
    /// </summary>
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("UserAccounts");
        builder.HasKey(account => account.ID);
        builder.Property(account => account.Username).HasMaxLength(64).IsRequired();
        builder.Property(account => account.NormalizedUsername).HasMaxLength(128).IsRequired();
        builder.Property(account => account.Nickname).HasMaxLength(64).IsRequired();
        builder.Property(account => account.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(account => account.IsAdmin).HasDefaultValue(false).IsRequired();
        builder.HasIndex(account => account.NormalizedUsername).IsUnique();
    }
}
