using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("UserAccounts");
        builder.HasKey(account => account.ID);
        builder.Property(account => account.Username).HasMaxLength(64).IsRequired();
        builder.Property(account => account.NormalizedUsername).HasMaxLength(128).IsRequired();
        builder.Property(account => account.Nickname).HasMaxLength(64).IsRequired();
        builder.Property(account => account.PasswordHash).HasMaxLength(512).IsRequired();
        builder.HasIndex(account => account.NormalizedUsername).IsUnique();
    }
}
