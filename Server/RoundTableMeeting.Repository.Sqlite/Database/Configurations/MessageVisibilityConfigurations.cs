using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;
namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class ParticipantMessageViewConfiguration : IEntityTypeConfiguration<ParticipantMessageView>
{
    public void Configure(EntityTypeBuilder<ParticipantMessageView> b)
    {
        b.ToTable("ParticipantMessageViews", t => t.HasCheckConstraint("CK_MessageViews_Sequence", "Sequence BETWEEN 1 AND 9007199254740991"));
        b.HasKey(x => x.ID);
        b.HasIndex(x => new { x.ParticipantId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.ParticipantId, x.MessageId }).IsUnique();
        b.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => new { x.ParticipantId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MeetingMessage>().WithMany().HasForeignKey(x => new { x.MessageId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ParticipantMessageCounterConfiguration : IEntityTypeConfiguration<ParticipantMessageCounter>
{
    public void Configure(EntityTypeBuilder<ParticipantMessageCounter> b)
    {
        b.ToTable("ParticipantMessageCounters", t => t.HasCheckConstraint("CK_MessageCounters_Watermark", "HighWatermark BETWEEN 0 AND 9007199254740991"));
        b.HasKey(x => x.ID); b.HasIndex(x => x.ParticipantId).IsUnique();
        b.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => new { x.ParticipantId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class MeetingAttachmentConfiguration : IEntityTypeConfiguration<MeetingAttachment>
{
    public void Configure(EntityTypeBuilder<MeetingAttachment> b)
    {
        b.ToTable("MeetingAttachments", t => {
            t.HasCheckConstraint("CK_Attachments_State", "(State = 'staged' AND MessageId IS NULL) OR (State = 'attached' AND MessageId IS NOT NULL)");
            t.HasCheckConstraint("CK_Attachments_Size", "SizeBytes >= 0");
        });
        b.HasKey(x => x.ID);
        b.Property(x => x.OriginalName).HasMaxLength(512).IsRequired();
        b.Property(x => x.MediaType).HasMaxLength(256).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(32).IsRequired();
        b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.StorageKey).IsUnique();
        b.HasIndex(x => new { x.MeetingId, x.UploaderParticipantId, x.UploadRequestId }).IsUnique();
        b.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => new { x.UploaderParticipantId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MeetingMessage>().WithMany().HasForeignKey(x => new { x.MessageId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
    }
}
