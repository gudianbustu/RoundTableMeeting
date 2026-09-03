using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class MeetingMessageConfiguration : IEntityTypeConfiguration<MeetingMessage>
{
    public void Configure(EntityTypeBuilder<MeetingMessage> builder)
    {
        builder.ToTable("MeetingMessages", table =>
        {
            table.HasCheckConstraint("CK_MeetingMessages_Sequence", "Sequence BETWEEN 1 AND 9007199254740991");
            table.HasCheckConstraint("CK_MeetingMessages_AuthorKind", "AuthorKind IN ('Human', 'Agent', 'System')");
            table.HasCheckConstraint("CK_MeetingMessages_AuthorParticipant", "(AuthorKind = 'System' AND AuthorParticipantId IS NULL) OR (AuthorKind IN ('Human', 'Agent') AND AuthorParticipantId IS NOT NULL)");
        });
        builder.HasKey(x => x.ID);
        builder.Property(x => x.AuthorKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.AuthorDisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Content).HasMaxLength(16384).IsRequired();
        builder.Property(x => x.Sequence).IsRequired();
        builder.Property(x => x.ClientSubmissionId).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.HasIndex(x => new { x.MeetingId, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.MeetingId, x.AuthorParticipantId, x.ClientSubmissionId }).IsUnique().HasFilter("AuthorParticipantId IS NOT NULL");
        builder.HasIndex(x => new { x.MeetingId, x.AuthorKind, x.ClientSubmissionId }).IsUnique().HasFilter("AuthorKind = 'System'");
        builder.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => new { x.AuthorParticipantId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MessageMentionConfiguration : IEntityTypeConfiguration<MessageMention>
{
    public void Configure(EntityTypeBuilder<MessageMention> builder)
    {
        builder.ToTable("MessageMentions", table => table.HasCheckConstraint("CK_MessageMentions_Range", "StartOffset >= 0 AND Length > 1"));
        builder.HasKey(x => x.ID);
        builder.Property(x => x.TargetDisplayName).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.MessageId, x.MeetingId, x.StartOffset }).IsUnique();
        builder.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MeetingMessage>().WithMany().HasForeignKey(x => new { x.MessageId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => new { x.TargetParticipantId, x.MeetingId }).HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MessageAppendedEventConfiguration : IEntityTypeConfiguration<MessageAppendedEvent>
{
    public void Configure(EntityTypeBuilder<MessageAppendedEvent> builder)
    {
        builder.ToTable("MessageAppendedEvents");
        builder.HasKey(x => x.ID);
        builder.HasIndex(x => x.MessageId).IsUnique();
        builder.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MeetingMessage>().WithMany().HasForeignKey(x => new { x.MessageId, x.MeetingId, x.Sequence }).HasPrincipalKey(x => new { x.ID, x.MeetingId, x.Sequence }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MessageEventDeliveryConfiguration : IEntityTypeConfiguration<MessageEventDelivery>
{
    public void Configure(EntityTypeBuilder<MessageEventDelivery> builder)
    {
        builder.ToTable("MessageEventDeliveries", table => table.HasCheckConstraint("CK_MessageEventDeliveries_State", "State IN ('Pending', 'Leased', 'Completed', 'Suspended')"));
        builder.HasKey(x => x.ID);
        builder.Property(x => x.ConsumerName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(x => new { x.EventId, x.ConsumerName }).IsUnique();
        builder.HasOne<MessageAppendedEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
