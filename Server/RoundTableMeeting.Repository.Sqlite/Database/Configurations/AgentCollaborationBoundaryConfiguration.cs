using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class AgentCollaborationBoundaryConfiguration : IEntityTypeConfiguration<AgentCollaborationBoundary>
{
    public void Configure(EntityTypeBuilder<AgentCollaborationBoundary> builder)
    {
        builder.ToTable("AgentCollaborationBoundaries", table => table.HasCheckConstraint(
            "CK_AgentCollaborationBoundaries_Sequence", "AgentTriggerStartSequence BETWEEN 0 AND 9007199254740991"));
        builder.HasKey(item => item.ID);
        builder.HasIndex(item => item.MeetingId).IsUnique();
        builder.HasOne<Meeting>().WithMany().HasForeignKey(item => item.MeetingId).OnDelete(DeleteBehavior.Restrict);
    }
}
