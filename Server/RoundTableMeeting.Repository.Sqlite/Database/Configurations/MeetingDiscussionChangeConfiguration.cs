using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class MeetingDiscussionChangeConfiguration : IEntityTypeConfiguration<MeetingDiscussionChange>
{
    public void Configure(EntityTypeBuilder<MeetingDiscussionChange> builder)
    {
        builder.ToTable("MeetingDiscussionChanges");
        builder.HasKey(change => change.ID);
        builder.HasIndex(change => new { change.MeetingId, change.Sequence }).IsUnique();
        builder.HasOne<Meeting>().WithMany().HasForeignKey(change => change.MeetingId).OnDelete(DeleteBehavior.Cascade);
    }
}
