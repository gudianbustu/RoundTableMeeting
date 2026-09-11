using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;
public sealed class AgentConnectionSessionConfiguration : IEntityTypeConfiguration<AgentConnectionSession>
{
	public void Configure(EntityTypeBuilder<AgentConnectionSession> builder)
	{
		builder.ToTable("AgentConnectionSessions", delegate(TableBuilder<AgentConnectionSession> table)
		{
			table.HasCheckConstraint("CK_AgentConnectionSessions_State", "State IN ('Connected', 'Disconnected', 'Activating', 'Superseded')");
			table.HasCheckConstraint("CK_AgentConnectionSessions_CharacterLimit", "DeclaredContextCharacterLimit > 0");
			table.HasCheckConstraint("CK_AgentConnectionSessions_StateTimes", "(State IN ('Connected', 'Activating') AND DisconnectedAtUtc IS NULL) OR (State IN ('Disconnected', 'Superseded') AND DisconnectedAtUtc IS NOT NULL)");
		});
		builder.HasKey((AgentConnectionSession x) => x.ID);
		builder.HasAlternateKey((AgentConnectionSession x) => new { x.ID, x.MeetingId });
		builder.Property((AgentConnectionSession x) => x.Transport).HasMaxLength(64).IsRequired();
		builder.Property((AgentConnectionSession x) => x.State).HasConversion<string>().HasMaxLength(16)
			.IsRequired();
		builder.Property((AgentConnectionSession x) => x.McpTransportBinding).HasMaxLength(128).IsRequired();
		builder.Property((AgentConnectionSession x) => x.DeclaredContextCharacterLimit).IsRequired();
		builder.Property((AgentConnectionSession x) => x.ConnectedAtUtc).IsRequired();
		builder.Property((AgentConnectionSession x) => x.LastActivityAtUtc).IsRequired();
		builder.Property((AgentConnectionSession x) => x.CreatedAtUtc).IsRequired();
		builder.HasOne<Meeting>().WithMany().HasForeignKey((AgentConnectionSession x) => x.MeetingId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey((AgentConnectionSession x) => new { x.ParticipantId, x.MeetingId })
			.HasPrincipalKey((MeetingParticipant x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex((AgentConnectionSession x) => new { x.MeetingId, x.ParticipantId, x.State });
		builder.HasIndex((AgentConnectionSession x) => new { x.MeetingId, x.ParticipantId }).IsUnique().HasFilter("State IN ('Connected', 'Activating')");
		builder.HasIndex((AgentConnectionSession x) => x.McpTransportBinding).IsUnique();
	}
}

public sealed class AgentContextCursorConfiguration : IEntityTypeConfiguration<AgentContextCursor>
{
	public void Configure(EntityTypeBuilder<AgentContextCursor> builder)
	{
		builder.ToTable("AgentContextCursors", delegate(TableBuilder<AgentContextCursor> table)
		{
			table.HasCheckConstraint("CK_AgentContextCursors_Sequences", "DeliveredThroughSequence BETWEEN 0 AND 9007199254740991 AND AllocatedThroughSequence BETWEEN 0 AND 9007199254740991 AND DeliveredThroughSequence <= AllocatedThroughSequence");
		});
		builder.HasKey((AgentContextCursor x) => x.ID);
		builder.HasAlternateKey((AgentContextCursor x) => new { x.ID, x.MeetingId });
		builder.Property((AgentContextCursor x) => x.DeliveredThroughSequence).IsRequired();
		builder.Property((AgentContextCursor x) => x.AllocatedThroughSequence).IsRequired();
		builder.Property((AgentContextCursor x) => x.UpdatedAtUtc).IsRequired();
		builder.HasOne<Meeting>().WithMany().HasForeignKey((AgentContextCursor x) => x.MeetingId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey((AgentContextCursor x) => new { x.ParticipantId, x.MeetingId })
			.HasPrincipalKey((MeetingParticipant x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex((AgentContextCursor x) => new { x.MeetingId, x.ParticipantId }).IsUnique();
	}
}

public sealed class AgentReplyTaskConfiguration : IEntityTypeConfiguration<AgentReplyTask>
{
	public void Configure(EntityTypeBuilder<AgentReplyTask> builder)
	{
		builder.ToTable("AgentReplyTasks", delegate(TableBuilder<AgentReplyTask> table)
		{
			table.HasCheckConstraint("CK_AgentReplyTasks_State", "State IN ('Queued', 'Delivered', 'Processing', 'Replied', 'Failed', 'Cancelled')");
			table.HasCheckConstraint("CK_AgentReplyTasks_CandidateRange", "CandidateFromSequenceExclusive BETWEEN 0 AND 9007199254740990 AND CandidateFromSequenceExclusive < CandidateToSequenceInclusive AND CandidateToSequenceInclusive = TriggerSequence AND TriggerSequence BETWEEN 1 AND 9007199254740991");
			table.HasCheckConstraint("CK_AgentReplyTasks_Source", "(SourceAuthorKind = 'System' AND SourceParticipantId IS NULL) OR (SourceAuthorKind IN ('Human', 'Agent') AND SourceParticipantId IS NOT NULL)");
			table.HasCheckConstraint("CK_AgentReplyTasks_Timestamps", "(State != 'Delivered' OR DeliveredAtUtc IS NOT NULL) AND (State != 'Processing' OR ProcessingAtUtc IS NOT NULL) AND (State != 'Replied' OR (RepliedAtUtc IS NOT NULL AND ReplyMessageId IS NOT NULL)) AND (State != 'Failed' OR (FailedAtUtc IS NOT NULL AND FailureCode IS NOT NULL)) AND (State != 'Cancelled' OR CancelledAtUtc IS NOT NULL)");
		});
		builder.HasKey((AgentReplyTask x) => x.ID);
		builder.HasAlternateKey((AgentReplyTask x) => new { x.ID, x.MeetingId });
		builder.Property((AgentReplyTask x) => x.SourceAuthorKind).HasConversion<string>().HasMaxLength(16)
			.IsRequired();
		builder.Property((AgentReplyTask x) => x.State).HasConversion<string>().HasMaxLength(16)
			.IsRequired();
		builder.Property((AgentReplyTask x) => x.FailureCode).HasMaxLength(128);
		builder.Property((AgentReplyTask x) => x.TriggerSequence).IsRequired();
		builder.Property((AgentReplyTask x) => x.CandidateFromSequenceExclusive).IsRequired();
		builder.Property((AgentReplyTask x) => x.CandidateToSequenceInclusive).IsRequired();
		builder.Property((AgentReplyTask x) => x.ReplyIdempotencyKey).IsRequired();
		builder.Property((AgentReplyTask x) => x.CreatedAtUtc).IsRequired();
		builder.HasOne<Meeting>().WithMany().HasForeignKey((AgentReplyTask x) => x.MeetingId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey((AgentReplyTask x) => new { x.AgentParticipantId, x.MeetingId })
			.HasPrincipalKey((MeetingParticipant x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MeetingMessage>().WithMany().HasForeignKey((AgentReplyTask x) => new { x.TriggerMessageId, x.MeetingId, x.TriggerSequence })
			.HasPrincipalKey((MeetingMessage x) => new { x.ID, x.MeetingId, x.Sequence })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MessageMention>().WithMany().HasForeignKey((AgentReplyTask x) => new { x.TriggerMentionId, x.MeetingId })
			.HasPrincipalKey((MessageMention x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey((AgentReplyTask x) => new { x.SourceParticipantId, x.MeetingId })
			.HasPrincipalKey((MeetingParticipant x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<MeetingMessage>().WithMany().HasForeignKey((AgentReplyTask x) => new { x.ReplyMessageId, x.MeetingId })
			.HasPrincipalKey((MeetingMessage x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex((AgentReplyTask x) => new { x.MeetingId, x.AgentParticipantId, x.TriggerMessageId }).IsUnique();
		builder.HasIndex((AgentReplyTask x) => x.ReplyIdempotencyKey).IsUnique();
		builder.HasIndex((AgentReplyTask x) => new { x.MeetingId, x.AgentParticipantId, x.State, x.TriggerSequence, x.ID });
	}
}

public sealed class AgentTaskDeliveryAttemptConfiguration : IEntityTypeConfiguration<AgentTaskDeliveryAttempt>
{
	public void Configure(EntityTypeBuilder<AgentTaskDeliveryAttempt> builder)
	{
		builder.ToTable("AgentTaskDeliveryAttempts", delegate(TableBuilder<AgentTaskDeliveryAttempt> table)
		{
			table.HasCheckConstraint("CK_AgentTaskDeliveryAttempts_State", "State IN ('Prepared', 'Delivered', 'Abandoned', 'Confirmed')");
			table.HasCheckConstraint("CK_AgentTaskDeliveryAttempts_CandidateRange", "CandidateFirstSequence BETWEEN 1 AND 9007199254740991 AND CandidateFirstSequence <= CandidateLastSequence AND CandidateLastSequence <= 9007199254740991");
			table.HasCheckConstraint("CK_AgentTaskDeliveryAttempts_CharacterCounts", "DeclaredContextCharacterLimit > 0 AND DeliveredCharacterCount >= 0 AND TruncatedMessageCount >= 0 AND ((Truncated = 0 AND TruncatedMessageCount = 0) OR (Truncated = 1 AND TruncatedMessageCount > 0)) AND ((OverLimit = 0 AND DeliveredCharacterCount <= DeclaredContextCharacterLimit) OR (OverLimit = 1 AND DeliveredCharacterCount > DeclaredContextCharacterLimit))");
			table.HasCheckConstraint("CK_AgentTaskDeliveryAttempts_DeliveredRange", "(State = 'Prepared' AND DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (State IN ('Delivered', 'Confirmed') AND DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL) OR (State = 'Abandoned' AND ((DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL)))");
		});
		builder.HasKey((AgentTaskDeliveryAttempt x) => x.ID);
		builder.Property((AgentTaskDeliveryAttempt x) => x.State).HasConversion<string>().HasMaxLength(16)
			.IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.CandidateFirstSequence).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.CandidateLastSequence).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.DeclaredContextCharacterLimit).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.DeliveredCharacterCount).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.Truncated).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.OverLimit).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.TruncatedMessageCount).IsRequired();
		builder.Property((AgentTaskDeliveryAttempt x) => x.CreatedAtUtc).IsRequired();
		builder.HasOne<Meeting>().WithMany().HasForeignKey((AgentTaskDeliveryAttempt x) => x.MeetingId)
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<AgentReplyTask>().WithMany().HasForeignKey((AgentTaskDeliveryAttempt x) => new { x.TaskId, x.MeetingId })
			.HasPrincipalKey((AgentReplyTask x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne<AgentConnectionSession>().WithMany().HasForeignKey((AgentTaskDeliveryAttempt x) => new { x.ConnectionSessionId, x.MeetingId })
			.HasPrincipalKey((AgentConnectionSession x) => new { x.ID, x.MeetingId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasIndex((AgentTaskDeliveryAttempt x) => new { x.TaskId, x.ID }).IsUnique();
		builder.HasIndex((AgentTaskDeliveryAttempt x) => new { x.MeetingId, x.ConnectionSessionId, x.State });
	}
}
