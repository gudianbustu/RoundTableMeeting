using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;
namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class MeetingDeletionOperationConfiguration : IEntityTypeConfiguration<MeetingDeletionOperation>
{
    public void Configure(EntityTypeBuilder<MeetingDeletionOperation> builder)
    {
        builder.ToTable("MeetingDeletionOperations");
        builder.HasKey(x => x.ID);
        builder.HasIndex(x => x.MeetingId).IsUnique();
    }
}
public sealed class MeetingLifecycleProofConfiguration : IEntityTypeConfiguration<MeetingLifecycleProof>
{
    public void Configure(EntityTypeBuilder<MeetingLifecycleProof> builder)
    {
        builder.ToTable("MeetingLifecycleProofs");
        builder.HasKey(x => x.ID);
        builder.Property(x => x.ProofHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.MeetingId, x.ProofHash }).IsUnique();
    }
}

public sealed class MeetingLeaveOperationConfiguration : IEntityTypeConfiguration<MeetingLeaveOperation>
{
    public void Configure(EntityTypeBuilder<MeetingLeaveOperation> builder)
    {
        builder.ToTable("MeetingLeaveOperations");
        builder.HasKey(x => x.ID);
        builder.HasIndex(x => new { x.MeetingId, x.ParticipantId, x.OperationId }).IsUnique();
        builder.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
    }
}
