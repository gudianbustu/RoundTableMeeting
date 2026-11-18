using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class MeetingDiscussionTurnConfiguration : IEntityTypeConfiguration<MeetingDiscussionTurn>
{
    public void Configure(EntityTypeBuilder<MeetingDiscussionTurn> builder)
    {
        builder.ToTable("MeetingDiscussionTurns");
        builder.HasKey(t => t.ID);
        builder.HasOne<MeetingMessage>().WithMany().HasForeignKey(t => t.ID).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.MeetingId, t.Sequence });
        builder.HasIndex(t => new { t.TopicId, t.Optional });
    }
}
