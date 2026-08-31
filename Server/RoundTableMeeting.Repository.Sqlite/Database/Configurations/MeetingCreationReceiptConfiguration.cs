using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class MeetingCreationReceiptConfiguration : IEntityTypeConfiguration<MeetingCreationReceipt>
{
    public void Configure(EntityTypeBuilder<MeetingCreationReceipt> builder)
    {
        builder.ToTable("MeetingCreationReceipts", table => table.HasCheckConstraint("CK_MeetingCreationReceipts_StateMeeting", "(State = 'Active' AND MeetingId IS NOT NULL) OR (State = 'Deleted' AND MeetingId IS NULL)"));
        builder.HasKey(x => x.ID);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(x => new { x.CreatorUserId, x.CreationRequestId }).IsUnique();
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Restrict);
    }
}
