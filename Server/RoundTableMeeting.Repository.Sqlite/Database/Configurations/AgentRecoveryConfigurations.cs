using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Repository.Sqlite.Database.Configurations;

public sealed class AgentConnectionRecoveryConfiguration : IEntityTypeConfiguration<AgentConnectionSession>
{
    public void Configure(EntityTypeBuilder<AgentConnectionSession> builder)
    {
        builder.Property(x => x.ConnectionGeneration).HasDefaultValue(1L);
        builder.Property(x => x.BindingVersion).HasDefaultValue(1L);
        builder.Property(x => x.ReliabilityMode).HasMaxLength(32).HasDefaultValue("legacy_v1");
        builder.Property(x => x.RecoveryKeyHash).HasMaxLength(64);
        builder.Property(x => x.DisconnectReason).HasMaxLength(64);
        builder.HasAlternateKey(x => new { x.ID, x.MeetingId, x.ParticipantId });
        builder.HasAlternateKey(x => new { x.ID, x.MeetingId, x.ParticipantId, x.ConnectionGeneration });
        builder.HasOne<AgentConnectionSession>().WithMany().HasForeignKey(x => new { x.SupersededByConnectionId, x.MeetingId, x.ParticipantId })
            .HasPrincipalKey(x => new { x.ID, x.MeetingId, x.ParticipantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.State, x.LeaseExpiresAtUtc });
        builder.ToTable("AgentConnectionSessions", table =>
        {
            table.HasCheckConstraint("CK_AgentConnections_Versions", "ConnectionGeneration BETWEEN 1 AND 9007199254740991 AND BindingVersion BETWEEN 1 AND 9007199254740991");
            table.HasCheckConstraint("CK_AgentConnections_Mode", "(ReliabilityMode = 'legacy_v1' AND RecoveryKeyHash IS NULL AND LeaseExpiresAtUtc IS NULL) OR (ReliabilityMode = 'explicit_ack_v1' AND RecoveryKeyHash IS NOT NULL AND length(RecoveryKeyHash) = 64 AND RecoveryKeyHash NOT GLOB '*[^0-9A-Fa-f]*' AND (State != 'Connected' OR LeaseExpiresAtUtc IS NOT NULL))");
            table.HasCheckConstraint("CK_AgentConnections_Replacement", "SupersededByConnectionId IS NULL OR (State = 'Superseded' AND SupersededByConnectionId != ID)");
        });
    }
}

public sealed class AgentCursorRecoveryConfiguration : IEntityTypeConfiguration<AgentContextCursor>
{
    public void Configure(EntityTypeBuilder<AgentContextCursor> builder)
    {
        builder.Property(x => x.RequiredReliabilityMode).HasMaxLength(32).HasDefaultValue("legacy_v1");
        builder.HasOne<AgentConnectionSession>().WithMany().HasForeignKey(x => new { x.CurrentConnectionId, x.MeetingId, x.ParticipantId, x.ConnectionGeneration })
            .HasPrincipalKey(x => new { x.ID, x.MeetingId, x.ParticipantId, x.ConnectionGeneration }).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable("AgentContextCursors", table =>
        {
            table.HasCheckConstraint("CK_AgentCursors_Recovery", "ConnectionGeneration BETWEEN 0 AND 9007199254740991 AND (CurrentConnectionId IS NULL OR ConnectionGeneration > 0) AND FirstJoinSnapshotSequence BETWEEN 0 AND AllocatedThroughSequence AND ActivityRevision BETWEEN 0 AND 9007199254740991 AND RequiredReliabilityMode IN ('legacy_v1', 'explicit_ack_v1')");
        });
    }
}

public sealed class AgentTaskRecoveryConfiguration : IEntityTypeConfiguration<AgentReplyTask>
{
    public void Configure(EntityTypeBuilder<AgentReplyTask> builder)
    {
        builder.Property(x => x.StopReason).HasConversion<string>().HasMaxLength(32);
        builder.HasOne<AgentTaskDeliveryAttempt>().WithMany().HasForeignKey(x => new { x.CurrentAttemptId, x.ID, x.MeetingId })
            .HasPrincipalKey(x => new { x.ID, x.TaskId, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.State, x.NextRetryAtUtc, x.TriggerSequence });
        builder.ToTable("AgentReplyTasks", table =>
        {
            table.HasCheckConstraint("CK_AgentTasks_Retry", "RetryCycle >= 0 AND AutomaticRetryCount >= 0 AND ((RetryExhausted = 0 AND StopReason IS NULL) OR (RetryExhausted = 1 AND StopReason IS NOT NULL AND StopReason IN ('AttemptsExhausted', 'PermanentFailure') AND NextRetryAtUtc IS NULL))");
            table.HasCheckConstraint("CK_AgentTasks_RetryTime", "NextRetryAtUtc IS NULL OR (State = 'Failed' AND FailedAtUtc IS NOT NULL AND NextRetryAtUtc >= FailedAtUtc)");
        });
    }
}

public sealed class AgentAttemptRecoveryConfiguration : IEntityTypeConfiguration<AgentTaskDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<AgentTaskDeliveryAttempt> builder)
    {
        builder.Property(x => x.ConnectionGeneration).HasDefaultValue(1L);
        builder.Property(x => x.BindingVersion).HasDefaultValue(1L);
        builder.Property(x => x.ContextFormatVersion).HasMaxLength(64).HasDefaultValue("agent-context-v1");
        builder.Property(x => x.InvalidationReason).HasMaxLength(64);
        builder.Property(x => x.ReportedFailureCode).HasMaxLength(64);
        builder.Property(x => x.FailureDecision).HasConversion(
            value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => value == null ? null : JsonSerializer.Deserialize<AgentReportedFailureDecision>(value, (JsonSerializerOptions?)null));
        builder.HasAlternateKey(x => new { x.ID, x.TaskId, x.MeetingId });
        builder.HasOne<AgentTaskDeliveryAttempt>().WithMany().HasForeignKey(x => new { x.ReplacementAttemptId, x.TaskId, x.MeetingId })
            .HasPrincipalKey(x => new { x.ID, x.TaskId, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.State, x.ConfirmationDeadlineUtc });
        builder.ToTable("AgentTaskDeliveryAttempts", table =>
        {
            table.HasCheckConstraint("CK_AgentAttempts_Versions", "ConnectionGeneration BETWEEN 1 AND 9007199254740991 AND BindingVersion BETWEEN 1 AND 9007199254740991 AND RetryCycle >= 0");
            table.HasCheckConstraint("CK_AgentAttempts_Confirmation", "(ConfirmedAtUtc IS NULL AND State != 'Confirmed') OR (ConfirmedAtUtc IS NOT NULL AND State IN ('Confirmed', 'Abandoned') AND ConfirmationDeadlineUtc IS NOT NULL AND ConfirmedAtUtc < ConfirmationDeadlineUtc AND ConfirmedAtUtc >= DeliveredAtUtc)");
            table.HasCheckConstraint("CK_AgentAttempts_Deadline", "ConfirmationDeadlineUtc IS NULL OR (DeliveredAtUtc IS NOT NULL AND ConfirmationDeadlineUtc > DeliveredAtUtc)");
            table.HasCheckConstraint("CK_AgentAttempts_Invalidation", "(InvalidatedAtUtc IS NULL AND InvalidationReason IS NULL AND ReplacementAttemptId IS NULL) OR (InvalidatedAtUtc IS NOT NULL AND InvalidationReason IS NOT NULL AND InvalidatedAtUtc >= CreatedAtUtc AND (ReplacementAttemptId IS NULL OR (State = 'Abandoned' AND ReplacementAttemptId != ID)))");
            table.HasCheckConstraint("CK_AgentAttempts_FailureReceipt", "(ReportedFailureCode IS NULL AND FailureDecision IS NULL) OR (ReportedFailureCode IN ('agent_transient_failure', 'agent_permanent_failure') AND FailureDecision IS NOT NULL AND json_valid(FailureDecision) AND ConfirmedAtUtc IS NOT NULL)");
        });
    }
}

public sealed class AgentInitialContextDeliveryConfiguration : IEntityTypeConfiguration<AgentInitialContextDelivery>
{
    public void Configure(EntityTypeBuilder<AgentInitialContextDelivery> builder)
    {
        builder.ToTable("AgentInitialContextDeliveries", table =>
        {
            table.HasCheckConstraint("CK_AgentInitial_Context", "SnapshotSequence BETWEEN 0 AND 9007199254740991 AND DeclaredCharacterLimit > 0 AND DeliveredCharacterCount >= 0 AND TruncatedMessageCount >= 0 AND ((Truncated = 0 AND TruncatedMessageCount = 0) OR (Truncated = 1 AND TruncatedMessageCount > 0)) AND ((OverLimit = 0 AND DeliveredCharacterCount <= DeclaredCharacterLimit) OR (OverLimit = 1 AND DeliveredCharacterCount > DeclaredCharacterLimit))");
            table.HasCheckConstraint("CK_AgentInitial_Range", "(SnapshotSequence = 0 AND CandidateFirstSequence IS NULL AND CandidateLastSequence IS NULL AND DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredCharacterCount = 0 AND TruncatedMessageCount = 0 AND Truncated = 0 AND OverLimit = 0) OR (SnapshotSequence > 0 AND CandidateFirstSequence IS NOT NULL AND CandidateLastSequence IS NOT NULL AND DeliveredFirstSequence IS NOT NULL AND DeliveredLastSequence IS NOT NULL AND DeliveredCharacterCount > 0 AND CandidateFirstSequence = 1 AND CandidateLastSequence = SnapshotSequence AND DeliveredFirstSequence BETWEEN 1 AND SnapshotSequence AND DeliveredLastSequence = SnapshotSequence AND TruncatedMessageCount = DeliveredFirstSequence - 1)");
            table.HasCheckConstraint("CK_AgentInitial_Confirmation", "(State = 'Prepared' AND ConfirmedAtUtc IS NULL) OR (State = 'Confirmed' AND ConfirmedAtUtc IS NOT NULL AND ConfirmedAtUtc >= CreatedAtUtc)");
        });
        builder.HasKey(x => x.ID);
        builder.Property(x => x.ContextFormatVersion).HasMaxLength(64).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
        builder.HasOne<AgentConnectionSession>().WithMany().HasForeignKey(x => new { x.ConnectionSessionId, x.MeetingId, x.ParticipantId })
            .HasPrincipalKey(x => new { x.ID, x.MeetingId, x.ParticipantId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ConnectionSessionId).IsUnique();
    }
}

public sealed class AgentRecoveryOperationConfiguration : IEntityTypeConfiguration<AgentRecoveryOperation>
{
    public void Configure(EntityTypeBuilder<AgentRecoveryOperation> builder)
    {
        builder.ToTable("AgentRecoveryOperations", table =>
        {
            table.HasCheckConstraint("CK_AgentOperations_Scope", "(OperationKind = 'Join' AND NormalizedDisplayName IS NOT NULL AND ConnectionSessionId IS NULL AND TaskId IS NULL AND ResultConnectionId IS NOT NULL AND ResultRetryCycle IS NULL) OR (OperationKind = 'Resume' AND NormalizedDisplayName IS NULL AND ConnectionSessionId IS NOT NULL AND ResultConnectionId IS NOT NULL AND ConnectionSessionId = ResultConnectionId AND TaskId IS NULL AND ResultRetryCycle IS NULL) OR (OperationKind = 'Retry' AND NormalizedDisplayName IS NULL AND ConnectionSessionId IS NULL AND TaskId IS NOT NULL AND ResultConnectionId IS NULL AND ResultRetryCycle IS NOT NULL AND ResultRetryCycle >= 1)");
            table.HasCheckConstraint("CK_AgentOperations_Result", "(OperationKind IN ('Join', 'Resume') AND ResultConnectionGeneration IS NOT NULL AND ResultBindingVersion IS NOT NULL AND ResultConnectionGeneration BETWEEN 1 AND 9007199254740991 AND ResultBindingVersion BETWEEN 1 AND 9007199254740991 AND TargetTransportBinding IS NOT NULL) OR (OperationKind = 'Retry' AND ResultConnectionGeneration IS NULL AND ResultBindingVersion IS NULL AND TargetTransportBinding IS NULL)");
            table.HasCheckConstraint("CK_AgentOperations_Fingerprint", "length(RequestFingerprint) = 64 AND RequestFingerprint NOT GLOB '*[^0-9A-Fa-f]*'");
        });
        builder.HasKey(x => x.ID);
        builder.Property(x => x.OperationKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.NormalizedDisplayName).HasMaxLength(128);
        builder.Property(x => x.TargetTransportBinding).HasMaxLength(128);
        builder.HasOne<MeetingParticipant>().WithMany().HasForeignKey(x => new { x.ParticipantId, x.MeetingId })
            .HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentConnectionSession>().WithMany().HasForeignKey(x => new { x.ResultConnectionId, x.MeetingId, x.ParticipantId, x.ResultConnectionGeneration })
            .HasPrincipalKey(x => new { x.ID, x.MeetingId, x.ParticipantId, x.ConnectionGeneration }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentReplyTask>().WithMany().HasForeignKey(x => new { x.TaskId, x.MeetingId })
            .HasPrincipalKey(x => new { x.ID, x.MeetingId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.MeetingId, x.NormalizedDisplayName, x.OperationId }).IsUnique().HasFilter("OperationKind = 'Join'");
        builder.HasIndex(x => new { x.MeetingId, x.ConnectionSessionId, x.OperationId }).IsUnique().HasFilter("OperationKind = 'Resume'");
        builder.HasIndex(x => new { x.MeetingId, x.ParticipantId, x.TaskId, x.OperationId }).IsUnique().HasFilter("OperationKind = 'Retry'");
    }
}
