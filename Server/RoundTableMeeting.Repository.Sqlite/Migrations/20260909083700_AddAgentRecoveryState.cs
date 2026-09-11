using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations;

public partial class AddAgentRecoveryState : Migration
{
    // Keep rebuilds, validation, copied data and migration history in the same EF transaction.
    // SQLite's generated rebuild script otherwise commits before replacing referenced tables.
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpgradeSql);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DowngradeSql);

    private const string UpgradeSql = """
PRAGMA defer_foreign_keys = ON;
CREATE TEMP TABLE recovery_migration_audit (Problem TEXT);
CREATE TEMP TRIGGER recovery_migration_abort BEFORE INSERT ON recovery_migration_audit
BEGIN SELECT RAISE(ABORT, NEW.Problem); END;
INSERT INTO recovery_migration_audit
SELECT 'Agent recovery migration: ambiguous participant ' || p.ID
FROM MeetingParticipants p WHERE p.ParticipantType = 'Agent' AND
((SELECT count(*) FROM AgentConnectionSessions c WHERE c.MeetingId = p.MeetingId AND c.ParticipantId = p.ID) != 1
 OR (SELECT count(*) FROM AgentContextCursors c WHERE c.MeetingId = p.MeetingId AND c.ParticipantId = p.ID) != 1);
INSERT INTO recovery_migration_audit
SELECT 'Agent recovery migration: ambiguous attempt for task ' || t.ID FROM AgentReplyTasks t
WHERE (SELECT count(*) FROM AgentTaskDeliveryAttempts a WHERE a.TaskId = t.ID
 AND a.CreatedAtUtc = (SELECT max(b.CreatedAtUtc) FROM AgentTaskDeliveryAttempts b WHERE b.TaskId = t.ID)) > 1
 OR (t.State IN ('Delivered', 'Processing', 'Replied') AND NOT EXISTS (SELECT 1 FROM AgentTaskDeliveryAttempts a WHERE a.TaskId = t.ID));
INSERT INTO recovery_migration_audit
SELECT 'Agent recovery migration: mismatched attempt ' || a.ID
FROM AgentTaskDeliveryAttempts a JOIN AgentReplyTasks t ON t.ID = a.TaskId
JOIN AgentConnectionSessions c ON c.ID = a.ConnectionSessionId
WHERE t.MeetingId != c.MeetingId OR t.AgentParticipantId != c.ParticipantId;
INSERT INTO recovery_migration_audit
SELECT 'Agent recovery migration: incomplete initial history for connection ' || c.ID
FROM AgentConnectionSessions c WHERE (SELECT count(*) FROM MeetingMessages m WHERE m.MeetingId = c.MeetingId AND m.Sequence <= c.InitialSnapshotSequence) != c.InitialSnapshotSequence;
DROP TRIGGER recovery_migration_abort;
DROP TABLE recovery_migration_audit;
CREATE TEMP TABLE recovery_legacy_connections AS SELECT * FROM AgentConnectionSessions;
DROP INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId";

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "BindingVersion" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "ConfirmationDeadlineUtc" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "ConfirmedAtUtc" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "ConnectionGeneration" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "ContextFormatVersion" TEXT NOT NULL DEFAULT 'agent-context-v1';

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "FailureDecision" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "InvalidatedAtUtc" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "InvalidationReason" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "ReplacementAttemptId" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "ReportedFailureCode" TEXT NULL;

ALTER TABLE "AgentTaskDeliveryAttempts" ADD "RetryCycle" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentReplyTasks" ADD "AutomaticRetryCount" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentReplyTasks" ADD "CurrentAttemptId" TEXT NULL;

ALTER TABLE "AgentReplyTasks" ADD "NextRetryAtUtc" TEXT NULL;

ALTER TABLE "AgentReplyTasks" ADD "RetryCycle" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentReplyTasks" ADD "RetryExhausted" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentReplyTasks" ADD "StopReason" TEXT NULL;

ALTER TABLE "AgentContextCursors" ADD "ActivityChangedAtUtc" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00+00:00';

ALTER TABLE "AgentContextCursors" ADD "ActivityRevision" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentContextCursors" ADD "ConnectionGeneration" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentContextCursors" ADD "CurrentConnectionId" TEXT NULL;

ALTER TABLE "AgentContextCursors" ADD "FirstJoinSnapshotSequence" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "AgentContextCursors" ADD "RequiredReliabilityMode" TEXT NOT NULL DEFAULT 'legacy_v1';

ALTER TABLE "AgentConnectionSessions" ADD "BindingVersion" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE "AgentConnectionSessions" ADD "ConnectionGeneration" INTEGER NOT NULL DEFAULT 1;

ALTER TABLE "AgentConnectionSessions" ADD "DisconnectReason" TEXT NULL;

ALTER TABLE "AgentConnectionSessions" ADD "LeaseExpiresAtUtc" TEXT NULL;

ALTER TABLE "AgentConnectionSessions" ADD "RecoveryKeyHash" TEXT NULL;

ALTER TABLE "AgentConnectionSessions" ADD "ReliabilityMode" TEXT NOT NULL DEFAULT 'legacy_v1';

ALTER TABLE "AgentConnectionSessions" ADD "SupersededByConnectionId" TEXT NULL;

CREATE UNIQUE INDEX recovery_bridge_attempt ON AgentTaskDeliveryAttempts(ID, TaskId, MeetingId);
CREATE UNIQUE INDEX recovery_bridge_connection ON AgentConnectionSessions(ID, MeetingId, ParticipantId);
CREATE UNIQUE INDEX recovery_bridge_generation ON AgentConnectionSessions(ID, MeetingId, ParticipantId, ConnectionGeneration);
CREATE TABLE "AgentInitialContextDeliveries" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentInitialContextDeliveries" PRIMARY KEY,
    "MeetingId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "ConnectionSessionId" TEXT NOT NULL,
    "SnapshotSequence" INTEGER NOT NULL,
    "CandidateFirstSequence" INTEGER NULL,
    "CandidateLastSequence" INTEGER NULL,
    "DeliveredFirstSequence" INTEGER NULL,
    "DeliveredLastSequence" INTEGER NULL,
    "DeclaredCharacterLimit" INTEGER NOT NULL,
    "DeliveredCharacterCount" INTEGER NOT NULL,
    "Truncated" INTEGER NOT NULL,
    "OverLimit" INTEGER NOT NULL,
    "TruncatedMessageCount" INTEGER NOT NULL,
    "ContextFormatVersion" TEXT NOT NULL,
    "State" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "ConfirmedAtUtc" TEXT NULL,
    CONSTRAINT "CK_AgentInitial_Confirmation" CHECK ((State = 'Prepared' AND ConfirmedAtUtc IS NULL) OR (State = 'Confirmed' AND ConfirmedAtUtc IS NOT NULL AND ConfirmedAtUtc >= CreatedAtUtc)),
    CONSTRAINT "CK_AgentInitial_Context" CHECK (SnapshotSequence BETWEEN 0 AND 9007199254740991 AND DeclaredCharacterLimit > 0 AND DeliveredCharacterCount >= 0 AND TruncatedMessageCount >= 0 AND ((Truncated = 0 AND TruncatedMessageCount = 0) OR (Truncated = 1 AND TruncatedMessageCount > 0)) AND ((OverLimit = 0 AND DeliveredCharacterCount <= DeclaredCharacterLimit) OR (OverLimit = 1 AND DeliveredCharacterCount > DeclaredCharacterLimit))),
    CONSTRAINT "CK_AgentInitial_Range" CHECK ((SnapshotSequence = 0 AND CandidateFirstSequence IS NULL AND CandidateLastSequence IS NULL AND DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredCharacterCount = 0 AND TruncatedMessageCount = 0 AND Truncated = 0 AND OverLimit = 0) OR (SnapshotSequence > 0 AND CandidateFirstSequence IS NOT NULL AND CandidateLastSequence IS NOT NULL AND DeliveredFirstSequence IS NOT NULL AND DeliveredLastSequence IS NOT NULL AND DeliveredCharacterCount > 0 AND CandidateFirstSequence = 1 AND CandidateLastSequence = SnapshotSequence AND DeliveredFirstSequence BETWEEN 1 AND SnapshotSequence AND DeliveredLastSequence = SnapshotSequence AND TruncatedMessageCount = DeliveredFirstSequence - 1)),
    CONSTRAINT "FK_AgentInitialContextDeliveries_AgentConnectionSessions_ConnectionSessionId_MeetingId_ParticipantId" FOREIGN KEY ("ConnectionSessionId", "MeetingId", "ParticipantId") REFERENCES "AgentConnectionSessions" ("ID", "MeetingId", "ParticipantId") ON DELETE RESTRICT
);

CREATE TABLE "AgentRecoveryOperations" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentRecoveryOperations" PRIMARY KEY,
    "MeetingId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "OperationKind" TEXT NOT NULL,
    "OperationId" TEXT NOT NULL,
    "RequestFingerprint" TEXT NOT NULL,
    "NormalizedDisplayName" TEXT NULL,
    "ConnectionSessionId" TEXT NULL,
    "TaskId" TEXT NULL,
    "ResultConnectionId" TEXT NULL,
    "ResultConnectionGeneration" INTEGER NULL,
    "ResultBindingVersion" INTEGER NULL,
    "TargetTransportBinding" TEXT NULL,
    "ResultRetryCycle" INTEGER NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "CK_AgentOperations_Fingerprint" CHECK (length(RequestFingerprint) = 64 AND RequestFingerprint NOT GLOB '*[^0-9A-Fa-f]*'),
    CONSTRAINT "CK_AgentOperations_Result" CHECK ((OperationKind IN ('Join', 'Resume') AND ResultConnectionGeneration IS NOT NULL AND ResultBindingVersion IS NOT NULL AND ResultConnectionGeneration BETWEEN 1 AND 9007199254740991 AND ResultBindingVersion BETWEEN 1 AND 9007199254740991 AND TargetTransportBinding IS NOT NULL) OR (OperationKind = 'Retry' AND ResultConnectionGeneration IS NULL AND ResultBindingVersion IS NULL AND TargetTransportBinding IS NULL)),
    CONSTRAINT "CK_AgentOperations_Scope" CHECK ((OperationKind = 'Join' AND NormalizedDisplayName IS NOT NULL AND ConnectionSessionId IS NULL AND TaskId IS NULL AND ResultConnectionId IS NOT NULL AND ResultRetryCycle IS NULL) OR (OperationKind = 'Resume' AND NormalizedDisplayName IS NULL AND ConnectionSessionId IS NOT NULL AND ResultConnectionId IS NOT NULL AND ConnectionSessionId = ResultConnectionId AND TaskId IS NULL AND ResultRetryCycle IS NULL) OR (OperationKind = 'Retry' AND NormalizedDisplayName IS NULL AND ConnectionSessionId IS NULL AND TaskId IS NOT NULL AND ResultConnectionId IS NULL AND ResultRetryCycle IS NOT NULL AND ResultRetryCycle >= 1)),
    CONSTRAINT "FK_AgentRecoveryOperations_AgentConnectionSessions_ResultConnectionId_MeetingId_ParticipantId_ResultConnectionGeneration" FOREIGN KEY ("ResultConnectionId", "MeetingId", "ParticipantId", "ResultConnectionGeneration") REFERENCES "AgentConnectionSessions" ("ID", "MeetingId", "ParticipantId", "ConnectionGeneration") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentRecoveryOperations_AgentReplyTasks_TaskId_MeetingId" FOREIGN KEY ("TaskId", "MeetingId") REFERENCES "AgentReplyTasks" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentRecoveryOperations_MeetingParticipants_ParticipantId_MeetingId" FOREIGN KEY ("ParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT
);

CREATE INDEX "IX_AgentTaskDeliveryAttempts_ReplacementAttemptId_TaskId_MeetingId" ON "AgentTaskDeliveryAttempts" ("ReplacementAttemptId", "TaskId", "MeetingId");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_State_ConfirmationDeadlineUtc" ON "AgentTaskDeliveryAttempts" ("State", "ConfirmationDeadlineUtc");

CREATE INDEX "IX_AgentReplyTasks_CurrentAttemptId_ID_MeetingId" ON "AgentReplyTasks" ("CurrentAttemptId", "ID", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_State_NextRetryAtUtc_TriggerSequence" ON "AgentReplyTasks" ("State", "NextRetryAtUtc", "TriggerSequence");

CREATE INDEX "IX_AgentContextCursors_CurrentConnectionId_MeetingId_ParticipantId_ConnectionGeneration" ON "AgentContextCursors" ("CurrentConnectionId", "MeetingId", "ParticipantId", "ConnectionGeneration");

CREATE UNIQUE INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId" ON "AgentConnectionSessions" ("MeetingId", "ParticipantId") WHERE State IN ('Connected', 'Activating');

CREATE INDEX "IX_AgentConnectionSessions_State_LeaseExpiresAtUtc" ON "AgentConnectionSessions" ("State", "LeaseExpiresAtUtc");

CREATE INDEX "IX_AgentConnectionSessions_SupersededByConnectionId_MeetingId_ParticipantId" ON "AgentConnectionSessions" ("SupersededByConnectionId", "MeetingId", "ParticipantId");

CREATE UNIQUE INDEX "IX_AgentInitialContextDeliveries_ConnectionSessionId" ON "AgentInitialContextDeliveries" ("ConnectionSessionId");

CREATE INDEX "IX_AgentInitialContextDeliveries_ConnectionSessionId_MeetingId_ParticipantId" ON "AgentInitialContextDeliveries" ("ConnectionSessionId", "MeetingId", "ParticipantId");

CREATE UNIQUE INDEX "IX_AgentRecoveryOperations_MeetingId_ConnectionSessionId_OperationId" ON "AgentRecoveryOperations" ("MeetingId", "ConnectionSessionId", "OperationId") WHERE OperationKind = 'Resume';

CREATE UNIQUE INDEX "IX_AgentRecoveryOperations_MeetingId_NormalizedDisplayName_OperationId" ON "AgentRecoveryOperations" ("MeetingId", "NormalizedDisplayName", "OperationId") WHERE OperationKind = 'Join';

CREATE UNIQUE INDEX "IX_AgentRecoveryOperations_MeetingId_ParticipantId_TaskId_OperationId" ON "AgentRecoveryOperations" ("MeetingId", "ParticipantId", "TaskId", "OperationId") WHERE OperationKind = 'Retry';

CREATE INDEX "IX_AgentRecoveryOperations_ParticipantId_MeetingId" ON "AgentRecoveryOperations" ("ParticipantId", "MeetingId");

CREATE INDEX "IX_AgentRecoveryOperations_ResultConnectionId_MeetingId_ParticipantId_ResultConnectionGeneration" ON "AgentRecoveryOperations" ("ResultConnectionId", "MeetingId", "ParticipantId", "ResultConnectionGeneration");

CREATE INDEX "IX_AgentRecoveryOperations_TaskId_MeetingId" ON "AgentRecoveryOperations" ("TaskId", "MeetingId");

CREATE TABLE "ef_temp_AgentTaskDeliveryAttempts" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentTaskDeliveryAttempts" PRIMARY KEY,
    "BindingVersion" INTEGER NOT NULL DEFAULT 1,
    "CandidateFirstSequence" INTEGER NOT NULL,
    "CandidateLastSequence" INTEGER NOT NULL,
    "ConfirmationDeadlineUtc" TEXT NULL,
    "ConfirmedAtUtc" TEXT NULL,
    "ConnectionGeneration" INTEGER NOT NULL DEFAULT 1,
    "ConnectionSessionId" TEXT NOT NULL,
    "ContextFormatVersion" TEXT NOT NULL DEFAULT 'agent-context-v1',
    "CreatedAtUtc" TEXT NOT NULL,
    "DeclaredContextCharacterLimit" INTEGER NOT NULL,
    "DeliveredAtUtc" TEXT NULL,
    "DeliveredCharacterCount" INTEGER NOT NULL,
    "DeliveredFirstSequence" INTEGER NULL,
    "DeliveredLastSequence" INTEGER NULL,
    "FailureDecision" TEXT NULL,
    "InvalidatedAtUtc" TEXT NULL,
    "InvalidationReason" TEXT NULL,
    "MeetingId" TEXT NOT NULL,
    "OverLimit" INTEGER NOT NULL,
    "ReplacementAttemptId" TEXT NULL,
    "ReportedFailureCode" TEXT NULL,
    "RetryCycle" INTEGER NOT NULL,
    "State" TEXT NOT NULL,
    "TaskId" TEXT NOT NULL,
    "Truncated" INTEGER NOT NULL,
    "TruncatedMessageCount" INTEGER NOT NULL,
    CONSTRAINT "AK_AgentTaskDeliveryAttempts_ID_TaskId_MeetingId" UNIQUE ("ID", "TaskId", "MeetingId"),
    CONSTRAINT "CK_AgentAttempts_Confirmation" CHECK ((ConfirmedAtUtc IS NULL AND State != 'Confirmed') OR (ConfirmedAtUtc IS NOT NULL AND State IN ('Confirmed', 'Abandoned') AND ConfirmationDeadlineUtc IS NOT NULL AND ConfirmedAtUtc < ConfirmationDeadlineUtc AND ConfirmedAtUtc >= DeliveredAtUtc)),
    CONSTRAINT "CK_AgentAttempts_Deadline" CHECK (ConfirmationDeadlineUtc IS NULL OR (DeliveredAtUtc IS NOT NULL AND ConfirmationDeadlineUtc > DeliveredAtUtc)),
    CONSTRAINT "CK_AgentAttempts_FailureReceipt" CHECK ((ReportedFailureCode IS NULL AND FailureDecision IS NULL) OR (ReportedFailureCode IN ('agent_transient_failure', 'agent_permanent_failure') AND FailureDecision IS NOT NULL AND json_valid(FailureDecision) AND ConfirmedAtUtc IS NOT NULL)),
    CONSTRAINT "CK_AgentAttempts_Invalidation" CHECK ((InvalidatedAtUtc IS NULL AND InvalidationReason IS NULL AND ReplacementAttemptId IS NULL) OR (InvalidatedAtUtc IS NOT NULL AND InvalidationReason IS NOT NULL AND InvalidatedAtUtc >= CreatedAtUtc AND (ReplacementAttemptId IS NULL OR (State = 'Abandoned' AND ReplacementAttemptId != ID)))),
    CONSTRAINT "CK_AgentAttempts_Versions" CHECK (ConnectionGeneration BETWEEN 1 AND 9007199254740991 AND BindingVersion BETWEEN 1 AND 9007199254740991 AND RetryCycle >= 0),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_CandidateRange" CHECK (CandidateFirstSequence BETWEEN 1 AND 9007199254740991 AND CandidateFirstSequence <= CandidateLastSequence AND CandidateLastSequence <= 9007199254740991),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_CharacterCounts" CHECK (DeclaredContextCharacterLimit > 0 AND DeliveredCharacterCount >= 0 AND TruncatedMessageCount >= 0 AND ((Truncated = 0 AND TruncatedMessageCount = 0) OR (Truncated = 1 AND TruncatedMessageCount > 0)) AND ((OverLimit = 0 AND DeliveredCharacterCount <= DeclaredContextCharacterLimit) OR (OverLimit = 1 AND DeliveredCharacterCount > DeclaredContextCharacterLimit))),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_DeliveredRange" CHECK ((State = 'Prepared' AND DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (State IN ('Delivered', 'Confirmed') AND DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL) OR (State = 'Abandoned' AND ((DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL)))),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_State" CHECK (State IN ('Prepared', 'Delivered', 'Abandoned', 'Confirmed')),
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_AgentConnectionSessions_ConnectionSessionId_MeetingId" FOREIGN KEY ("ConnectionSessionId", "MeetingId") REFERENCES "AgentConnectionSessions" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_AgentReplyTasks_TaskId_MeetingId" FOREIGN KEY ("TaskId", "MeetingId") REFERENCES "AgentReplyTasks" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_AgentTaskDeliveryAttempts_ReplacementAttemptId_TaskId_MeetingId" FOREIGN KEY ("ReplacementAttemptId", "TaskId", "MeetingId") REFERENCES "AgentTaskDeliveryAttempts" ("ID", "TaskId", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentTaskDeliveryAttempts" ("ID", "BindingVersion", "CandidateFirstSequence", "CandidateLastSequence", "ConfirmationDeadlineUtc", "ConfirmedAtUtc", "ConnectionGeneration", "ConnectionSessionId", "ContextFormatVersion", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DeliveredAtUtc", "DeliveredCharacterCount", "DeliveredFirstSequence", "DeliveredLastSequence", "FailureDecision", "InvalidatedAtUtc", "InvalidationReason", "MeetingId", "OverLimit", "ReplacementAttemptId", "ReportedFailureCode", "RetryCycle", "State", "TaskId", "Truncated", "TruncatedMessageCount")
SELECT "ID", "BindingVersion", "CandidateFirstSequence", "CandidateLastSequence", "ConfirmationDeadlineUtc", "ConfirmedAtUtc", "ConnectionGeneration", "ConnectionSessionId", "ContextFormatVersion", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DeliveredAtUtc", "DeliveredCharacterCount", "DeliveredFirstSequence", "DeliveredLastSequence", "FailureDecision", "InvalidatedAtUtc", "InvalidationReason", "MeetingId", "OverLimit", "ReplacementAttemptId", "ReportedFailureCode", "RetryCycle", "State", "TaskId", "Truncated", "TruncatedMessageCount"
FROM "AgentTaskDeliveryAttempts";

CREATE TABLE "ef_temp_AgentConnectionSessions" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentConnectionSessions" PRIMARY KEY,
    "BindingVersion" INTEGER NOT NULL DEFAULT 1,
    "ConnectedAtUtc" TEXT NOT NULL,
    "ConnectionGeneration" INTEGER NOT NULL DEFAULT 1,
    "CreatedAtUtc" TEXT NOT NULL,
    "DeclaredContextCharacterLimit" INTEGER NOT NULL,
    "DisconnectReason" TEXT NULL,
    "DisconnectedAtUtc" TEXT NULL,
    "LastActivityAtUtc" TEXT NOT NULL,
    "LeaseExpiresAtUtc" TEXT NULL,
    "McpTransportBinding" TEXT NOT NULL,
    "MeetingId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "RecoveryKeyHash" TEXT NULL,
    "ReliabilityMode" TEXT NOT NULL DEFAULT 'legacy_v1',
    "State" TEXT NOT NULL,
    "SupersededByConnectionId" TEXT NULL,
    "Transport" TEXT NOT NULL,
    CONSTRAINT "AK_AgentConnectionSessions_ID_MeetingId" UNIQUE ("ID", "MeetingId"),
    CONSTRAINT "AK_AgentConnectionSessions_ID_MeetingId_ParticipantId" UNIQUE ("ID", "MeetingId", "ParticipantId"),
    CONSTRAINT "AK_AgentConnectionSessions_ID_MeetingId_ParticipantId_ConnectionGeneration" UNIQUE ("ID", "MeetingId", "ParticipantId", "ConnectionGeneration"),
    CONSTRAINT "CK_AgentConnections_Mode" CHECK ((ReliabilityMode = 'legacy_v1' AND RecoveryKeyHash IS NULL AND LeaseExpiresAtUtc IS NULL) OR (ReliabilityMode = 'explicit_ack_v1' AND RecoveryKeyHash IS NOT NULL AND length(RecoveryKeyHash) = 64 AND RecoveryKeyHash NOT GLOB '*[^0-9A-Fa-f]*' AND (State != 'Connected' OR LeaseExpiresAtUtc IS NOT NULL))),
    CONSTRAINT "CK_AgentConnections_Replacement" CHECK (SupersededByConnectionId IS NULL OR (State = 'Superseded' AND SupersededByConnectionId != ID)),
    CONSTRAINT "CK_AgentConnections_Versions" CHECK (ConnectionGeneration BETWEEN 1 AND 9007199254740991 AND BindingVersion BETWEEN 1 AND 9007199254740991),
    CONSTRAINT "CK_AgentConnectionSessions_CharacterLimit" CHECK (DeclaredContextCharacterLimit > 0),
    CONSTRAINT "CK_AgentConnectionSessions_State" CHECK (State IN ('Connected', 'Disconnected', 'Activating', 'Superseded')),
    CONSTRAINT "CK_AgentConnectionSessions_StateTimes" CHECK ((State IN ('Connected', 'Activating') AND DisconnectedAtUtc IS NULL) OR (State IN ('Disconnected', 'Superseded') AND DisconnectedAtUtc IS NOT NULL)),
    CONSTRAINT "FK_AgentConnectionSessions_AgentConnectionSessions_SupersededByConnectionId_MeetingId_ParticipantId" FOREIGN KEY ("SupersededByConnectionId", "MeetingId", "ParticipantId") REFERENCES "AgentConnectionSessions" ("ID", "MeetingId", "ParticipantId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentConnectionSessions_MeetingParticipants_ParticipantId_MeetingId" FOREIGN KEY ("ParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentConnectionSessions_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentConnectionSessions" ("ID", "BindingVersion", "ConnectedAtUtc", "ConnectionGeneration", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DisconnectReason", "DisconnectedAtUtc", "LastActivityAtUtc", "LeaseExpiresAtUtc", "McpTransportBinding", "MeetingId", "ParticipantId", "RecoveryKeyHash", "ReliabilityMode", "State", "SupersededByConnectionId", "Transport")
SELECT "ID", "BindingVersion", "ConnectedAtUtc", "ConnectionGeneration", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DisconnectReason", "DisconnectedAtUtc", "LastActivityAtUtc", "LeaseExpiresAtUtc", "McpTransportBinding", "MeetingId", "ParticipantId", "RecoveryKeyHash", "ReliabilityMode", "State", "SupersededByConnectionId", "Transport"
FROM "AgentConnectionSessions";

CREATE TABLE "ef_temp_AgentReplyTasks" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentReplyTasks" PRIMARY KEY,
    "AgentParticipantId" TEXT NOT NULL,
    "AutomaticRetryCount" INTEGER NOT NULL,
    "CancelledAtUtc" TEXT NULL,
    "CandidateFromSequenceExclusive" INTEGER NOT NULL,
    "CandidateToSequenceInclusive" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "CurrentAttemptId" TEXT NULL,
    "DeliveredAtUtc" TEXT NULL,
    "FailedAtUtc" TEXT NULL,
    "FailureCode" TEXT NULL,
    "MeetingId" TEXT NOT NULL,
    "NextRetryAtUtc" TEXT NULL,
    "ProcessingAtUtc" TEXT NULL,
    "RepliedAtUtc" TEXT NULL,
    "ReplyIdempotencyKey" TEXT NOT NULL,
    "ReplyMessageId" TEXT NULL,
    "RetryCycle" INTEGER NOT NULL,
    "RetryExhausted" INTEGER NOT NULL,
    "SourceAuthorKind" TEXT NOT NULL,
    "SourceParticipantId" TEXT NULL,
    "State" TEXT NOT NULL,
    "StopReason" TEXT NULL,
    "TriggerMentionId" TEXT NOT NULL,
    "TriggerMessageId" TEXT NOT NULL,
    "TriggerSequence" INTEGER NOT NULL,
    CONSTRAINT "AK_AgentReplyTasks_ID_MeetingId" UNIQUE ("ID", "MeetingId"),
    CONSTRAINT "CK_AgentReplyTasks_CandidateRange" CHECK (CandidateFromSequenceExclusive BETWEEN 0 AND 9007199254740990 AND CandidateFromSequenceExclusive < CandidateToSequenceInclusive AND CandidateToSequenceInclusive = TriggerSequence AND TriggerSequence BETWEEN 1 AND 9007199254740991),
    CONSTRAINT "CK_AgentReplyTasks_Source" CHECK ((SourceAuthorKind = 'System' AND SourceParticipantId IS NULL) OR (SourceAuthorKind IN ('Human', 'Agent') AND SourceParticipantId IS NOT NULL)),
    CONSTRAINT "CK_AgentReplyTasks_State" CHECK (State IN ('Queued', 'Delivered', 'Processing', 'Replied', 'Failed', 'Cancelled')),
    CONSTRAINT "CK_AgentReplyTasks_Timestamps" CHECK ((State != 'Delivered' OR DeliveredAtUtc IS NOT NULL) AND (State != 'Processing' OR ProcessingAtUtc IS NOT NULL) AND (State != 'Replied' OR (RepliedAtUtc IS NOT NULL AND ReplyMessageId IS NOT NULL)) AND (State != 'Failed' OR (FailedAtUtc IS NOT NULL AND FailureCode IS NOT NULL)) AND (State != 'Cancelled' OR CancelledAtUtc IS NOT NULL)),
    CONSTRAINT "CK_AgentTasks_Retry" CHECK (RetryCycle >= 0 AND AutomaticRetryCount >= 0 AND ((RetryExhausted = 0 AND StopReason IS NULL) OR (RetryExhausted = 1 AND StopReason IS NOT NULL AND StopReason IN ('AttemptsExhausted', 'PermanentFailure') AND NextRetryAtUtc IS NULL))),
    CONSTRAINT "CK_AgentTasks_RetryTime" CHECK (NextRetryAtUtc IS NULL OR (State = 'Failed' AND FailedAtUtc IS NOT NULL AND NextRetryAtUtc >= FailedAtUtc)),
    CONSTRAINT "FK_AgentReplyTasks_AgentTaskDeliveryAttempts_CurrentAttemptId_ID_MeetingId" FOREIGN KEY ("CurrentAttemptId", "ID", "MeetingId") REFERENCES "AgentTaskDeliveryAttempts" ("ID", "TaskId", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_ReplyMessageId_MeetingId" FOREIGN KEY ("ReplyMessageId", "MeetingId") REFERENCES "MeetingMessages" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_TriggerMessageId_MeetingId_TriggerSequence" FOREIGN KEY ("TriggerMessageId", "MeetingId", "TriggerSequence") REFERENCES "MeetingMessages" ("ID", "MeetingId", "Sequence") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingParticipants_AgentParticipantId_MeetingId" FOREIGN KEY ("AgentParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingParticipants_SourceParticipantId_MeetingId" FOREIGN KEY ("SourceParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MessageMentions_TriggerMentionId_MeetingId" FOREIGN KEY ("TriggerMentionId", "MeetingId") REFERENCES "MessageMentions" ("ID", "MeetingId") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentReplyTasks" ("ID", "AgentParticipantId", "AutomaticRetryCount", "CancelledAtUtc", "CandidateFromSequenceExclusive", "CandidateToSequenceInclusive", "CreatedAtUtc", "CurrentAttemptId", "DeliveredAtUtc", "FailedAtUtc", "FailureCode", "MeetingId", "NextRetryAtUtc", "ProcessingAtUtc", "RepliedAtUtc", "ReplyIdempotencyKey", "ReplyMessageId", "RetryCycle", "RetryExhausted", "SourceAuthorKind", "SourceParticipantId", "State", "StopReason", "TriggerMentionId", "TriggerMessageId", "TriggerSequence")
SELECT "ID", "AgentParticipantId", "AutomaticRetryCount", "CancelledAtUtc", "CandidateFromSequenceExclusive", "CandidateToSequenceInclusive", "CreatedAtUtc", "CurrentAttemptId", "DeliveredAtUtc", "FailedAtUtc", "FailureCode", "MeetingId", "NextRetryAtUtc", "ProcessingAtUtc", "RepliedAtUtc", "ReplyIdempotencyKey", "ReplyMessageId", "RetryCycle", "RetryExhausted", "SourceAuthorKind", "SourceParticipantId", "State", "StopReason", "TriggerMentionId", "TriggerMessageId", "TriggerSequence"
FROM "AgentReplyTasks";

CREATE TABLE "ef_temp_AgentContextCursors" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentContextCursors" PRIMARY KEY,
    "ActivityChangedAtUtc" TEXT NOT NULL,
    "ActivityRevision" INTEGER NOT NULL,
    "AllocatedThroughSequence" INTEGER NOT NULL,
    "ConnectionGeneration" INTEGER NOT NULL,
    "CurrentConnectionId" TEXT NULL,
    "DeliveredThroughSequence" INTEGER NOT NULL,
    "FirstJoinSnapshotSequence" INTEGER NOT NULL,
    "MeetingId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "RequiredReliabilityMode" TEXT NOT NULL DEFAULT 'legacy_v1',
    "UpdatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "AK_AgentContextCursors_ID_MeetingId" UNIQUE ("ID", "MeetingId"),
    CONSTRAINT "CK_AgentContextCursors_Sequences" CHECK (DeliveredThroughSequence BETWEEN 0 AND 9007199254740991 AND AllocatedThroughSequence BETWEEN 0 AND 9007199254740991 AND DeliveredThroughSequence <= AllocatedThroughSequence),
    CONSTRAINT "CK_AgentCursors_Recovery" CHECK (ConnectionGeneration BETWEEN 0 AND 9007199254740991 AND (CurrentConnectionId IS NULL OR ConnectionGeneration > 0) AND FirstJoinSnapshotSequence BETWEEN 0 AND AllocatedThroughSequence AND ActivityRevision BETWEEN 0 AND 9007199254740991 AND RequiredReliabilityMode IN ('legacy_v1', 'explicit_ack_v1')),
    CONSTRAINT "FK_AgentContextCursors_AgentConnectionSessions_CurrentConnectionId_MeetingId_ParticipantId_ConnectionGeneration" FOREIGN KEY ("CurrentConnectionId", "MeetingId", "ParticipantId", "ConnectionGeneration") REFERENCES "AgentConnectionSessions" ("ID", "MeetingId", "ParticipantId", "ConnectionGeneration") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentContextCursors_MeetingParticipants_ParticipantId_MeetingId" FOREIGN KEY ("ParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentContextCursors_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentContextCursors" ("ID", "ActivityChangedAtUtc", "ActivityRevision", "AllocatedThroughSequence", "ConnectionGeneration", "CurrentConnectionId", "DeliveredThroughSequence", "FirstJoinSnapshotSequence", "MeetingId", "ParticipantId", "RequiredReliabilityMode", "UpdatedAtUtc")
SELECT "ID", "ActivityChangedAtUtc", "ActivityRevision", "AllocatedThroughSequence", "ConnectionGeneration", "CurrentConnectionId", "DeliveredThroughSequence", "FirstJoinSnapshotSequence", "MeetingId", "ParticipantId", "RequiredReliabilityMode", "UpdatedAtUtc"
FROM "AgentContextCursors";



DROP TABLE "AgentTaskDeliveryAttempts";

ALTER TABLE "ef_temp_AgentTaskDeliveryAttempts" RENAME TO "AgentTaskDeliveryAttempts";

DROP TABLE "AgentConnectionSessions";

ALTER TABLE "ef_temp_AgentConnectionSessions" RENAME TO "AgentConnectionSessions";

DROP TABLE "AgentReplyTasks";

ALTER TABLE "ef_temp_AgentReplyTasks" RENAME TO "AgentReplyTasks";

DROP TABLE "AgentContextCursors";

ALTER TABLE "ef_temp_AgentContextCursors" RENAME TO "AgentContextCursors";



CREATE INDEX "IX_AgentTaskDeliveryAttempts_ConnectionSessionId_MeetingId" ON "AgentTaskDeliveryAttempts" ("ConnectionSessionId", "MeetingId");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_MeetingId_ConnectionSessionId_State" ON "AgentTaskDeliveryAttempts" ("MeetingId", "ConnectionSessionId", "State");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_ReplacementAttemptId_TaskId_MeetingId" ON "AgentTaskDeliveryAttempts" ("ReplacementAttemptId", "TaskId", "MeetingId");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_State_ConfirmationDeadlineUtc" ON "AgentTaskDeliveryAttempts" ("State", "ConfirmationDeadlineUtc");

CREATE UNIQUE INDEX "IX_AgentTaskDeliveryAttempts_TaskId_ID" ON "AgentTaskDeliveryAttempts" ("TaskId", "ID");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_TaskId_MeetingId" ON "AgentTaskDeliveryAttempts" ("TaskId", "MeetingId");

CREATE UNIQUE INDEX "IX_AgentConnectionSessions_McpTransportBinding" ON "AgentConnectionSessions" ("McpTransportBinding");

CREATE UNIQUE INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId" ON "AgentConnectionSessions" ("MeetingId", "ParticipantId") WHERE State IN ('Connected', 'Activating');

CREATE INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId_State" ON "AgentConnectionSessions" ("MeetingId", "ParticipantId", "State");

CREATE INDEX "IX_AgentConnectionSessions_ParticipantId_MeetingId" ON "AgentConnectionSessions" ("ParticipantId", "MeetingId");

CREATE INDEX "IX_AgentConnectionSessions_State_LeaseExpiresAtUtc" ON "AgentConnectionSessions" ("State", "LeaseExpiresAtUtc");

CREATE INDEX "IX_AgentConnectionSessions_SupersededByConnectionId_MeetingId_ParticipantId" ON "AgentConnectionSessions" ("SupersededByConnectionId", "MeetingId", "ParticipantId");

CREATE INDEX "IX_AgentReplyTasks_AgentParticipantId_MeetingId" ON "AgentReplyTasks" ("AgentParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_CurrentAttemptId_ID_MeetingId" ON "AgentReplyTasks" ("CurrentAttemptId", "ID", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_State_TriggerSequence_ID" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "State", "TriggerSequence", "ID");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerMessageId" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "TriggerMessageId");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_ReplyIdempotencyKey" ON "AgentReplyTasks" ("ReplyIdempotencyKey");

CREATE INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId" ON "AgentReplyTasks" ("ReplyMessageId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_SourceParticipantId_MeetingId" ON "AgentReplyTasks" ("SourceParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_State_NextRetryAtUtc_TriggerSequence" ON "AgentReplyTasks" ("State", "NextRetryAtUtc", "TriggerSequence");

CREATE INDEX "IX_AgentReplyTasks_TriggerMentionId_MeetingId" ON "AgentReplyTasks" ("TriggerMentionId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_TriggerMessageId_MeetingId_TriggerSequence" ON "AgentReplyTasks" ("TriggerMessageId", "MeetingId", "TriggerSequence");

CREATE INDEX "IX_AgentContextCursors_CurrentConnectionId_MeetingId_ParticipantId_ConnectionGeneration" ON "AgentContextCursors" ("CurrentConnectionId", "MeetingId", "ParticipantId", "ConnectionGeneration");

CREATE UNIQUE INDEX "IX_AgentContextCursors_MeetingId_ParticipantId" ON "AgentContextCursors" ("MeetingId", "ParticipantId");

CREATE INDEX "IX_AgentContextCursors_ParticipantId_MeetingId" ON "AgentContextCursors" ("ParticipantId", "MeetingId");


UPDATE AgentContextCursors SET
CurrentConnectionId = (SELECT c.ID FROM recovery_legacy_connections c WHERE c.MeetingId = AgentContextCursors.MeetingId AND c.ParticipantId = AgentContextCursors.ParticipantId),
ConnectionGeneration = 1,
FirstJoinSnapshotSequence = (SELECT c.InitialSnapshotSequence FROM recovery_legacy_connections c WHERE c.MeetingId = AgentContextCursors.MeetingId AND c.ParticipantId = AgentContextCursors.ParticipantId),
ActivityChangedAtUtc = UpdatedAtUtc;
UPDATE AgentReplyTasks SET CurrentAttemptId = (SELECT a.ID FROM AgentTaskDeliveryAttempts a WHERE a.TaskId = AgentReplyTasks.ID ORDER BY a.CreatedAtUtc DESC LIMIT 1);
-- Legacy failures have no automatic retry contract. Preserve the original cause and
-- failure timestamp; explicit retry is required after a reliable takeover.
UPDATE AgentReplyTasks SET RetryExhausted = 1, StopReason = 'PermanentFailure' WHERE State = 'Failed';
-- Reconstruct the frozen v1 window using UTF-16 character counts, including supplementary characters.
CREATE TEMP TABLE recovery_message_lengths (MeetingId TEXT, Sequence INTEGER, Characters INTEGER);
WITH RECURSIVE formatted AS (
 SELECT m.MeetingId, m.Sequence,
 'messageId=' || lower(m.ID) || char(10) || 'sequence=' || m.Sequence || char(10) ||
 'authorKind=' || lower(m.AuthorKind) || char(10) ||
 'authorDisplayName=' || replace(replace(replace(m.AuthorDisplayName, '\', '\\'), char(13), '\r'), char(10), '\n') || char(10) ||
 'content=' || replace(replace(replace(m.Content, '\', '\\'), char(13), '\r'), char(10), '\n') || char(10) AS Block
 FROM MeetingMessages m WHERE EXISTS (SELECT 1 FROM recovery_legacy_connections c WHERE c.MeetingId = m.MeetingId AND c.InitialSnapshotSequence >= m.Sequence)
), characters(MeetingId, Sequence, Block, Position, Supplementary) AS (
 SELECT MeetingId, Sequence, Block, 1, 0 FROM formatted
 UNION ALL SELECT MeetingId, Sequence, Block, Position + 1,
 Supplementary + CASE WHEN unicode(substr(Block, Position, 1)) > 65535 THEN 1 ELSE 0 END
 FROM characters WHERE Position <= length(Block)
)
INSERT INTO recovery_message_lengths SELECT MeetingId, Sequence, length(Block) + Supplementary FROM characters WHERE Position > length(Block);
WITH candidate AS (
 SELECT c.ID AS ConnectionId, m.Sequence, m.Characters, c.InitialSnapshotSequence, c.DeclaredContextCharacterLimit,
 sum(m.Characters) OVER (PARTITION BY c.ID ORDER BY m.Sequence DESC) AS Cumulative
 FROM recovery_legacy_connections c JOIN recovery_message_lengths m ON m.MeetingId = c.MeetingId AND m.Sequence <= c.InitialSnapshotSequence
), selected AS (
 SELECT ConnectionId, min(Sequence) AS FirstSequence, sum(Characters) AS Characters FROM candidate
 WHERE Cumulative <= DeclaredContextCharacterLimit OR Sequence = InitialSnapshotSequence GROUP BY ConnectionId
), identities AS (SELECT ID, hex(randomblob(16)) AS NewId FROM recovery_legacy_connections)
INSERT INTO AgentInitialContextDeliveries
(ID, MeetingId, ParticipantId, ConnectionSessionId, SnapshotSequence, CandidateFirstSequence, CandidateLastSequence,
 DeliveredFirstSequence, DeliveredLastSequence, DeclaredCharacterLimit, DeliveredCharacterCount, Truncated, OverLimit,
 TruncatedMessageCount, ContextFormatVersion, State, CreatedAtUtc, ConfirmedAtUtc)
SELECT substr(i.NewId,1,8)||'-'||substr(i.NewId,9,4)||'-'||substr(i.NewId,13,4)||'-'||substr(i.NewId,17,4)||'-'||substr(i.NewId,21,12),
 c.MeetingId, c.ParticipantId, c.ID, c.InitialSnapshotSequence,
 CASE WHEN c.InitialSnapshotSequence = 0 THEN NULL ELSE 1 END, nullif(c.InitialSnapshotSequence,0), s.FirstSequence,
 nullif(c.InitialSnapshotSequence,0), c.DeclaredContextCharacterLimit, coalesce(s.Characters,0), coalesce(s.FirstSequence > 1,0),
 coalesce(s.Characters > c.DeclaredContextCharacterLimit,0), coalesce(s.FirstSequence-1,0), 'agent-context-v1',
 c.InitialContextState, c.CreatedAtUtc, c.InitialContextConfirmedAtUtc
FROM recovery_legacy_connections c JOIN identities i ON i.ID = c.ID LEFT JOIN selected s ON s.ConnectionId = c.ID;
DROP TABLE recovery_message_lengths;
DROP TABLE recovery_legacy_connections;
CREATE TRIGGER agent_attempt_owner_insert BEFORE INSERT ON AgentTaskDeliveryAttempts
WHEN EXISTS (SELECT 1 FROM AgentReplyTasks t JOIN AgentConnectionSessions c ON c.ID = NEW.ConnectionSessionId
 WHERE t.ID = NEW.TaskId AND (t.AgentParticipantId != c.ParticipantId OR t.MeetingId != c.MeetingId))
BEGIN SELECT RAISE(ABORT, 'Agent attempt owner mismatch'); END;
CREATE TRIGGER agent_attempt_owner_update BEFORE UPDATE OF TaskId, ConnectionSessionId, MeetingId ON AgentTaskDeliveryAttempts
WHEN EXISTS (SELECT 1 FROM AgentReplyTasks t JOIN AgentConnectionSessions c ON c.ID = NEW.ConnectionSessionId
 WHERE t.ID = NEW.TaskId AND (t.AgentParticipantId != c.ParticipantId OR t.MeetingId != c.MeetingId))
BEGIN SELECT RAISE(ABORT, 'Agent attempt owner mismatch'); END;
CREATE TRIGGER agent_task_owner_update BEFORE UPDATE OF AgentParticipantId, MeetingId ON AgentReplyTasks
WHEN EXISTS (SELECT 1 FROM AgentTaskDeliveryAttempts a JOIN AgentConnectionSessions c ON c.ID = a.ConnectionSessionId
 WHERE a.TaskId = NEW.ID AND (c.ParticipantId != NEW.AgentParticipantId OR c.MeetingId != NEW.MeetingId))
 OR EXISTS (SELECT 1 FROM AgentRecoveryOperations r WHERE r.TaskId = NEW.ID AND r.ParticipantId != NEW.AgentParticipantId)
BEGIN SELECT RAISE(ABORT, 'Agent task owner mismatch'); END;
CREATE TRIGGER agent_connection_owner_update BEFORE UPDATE OF ParticipantId, MeetingId ON AgentConnectionSessions
WHEN EXISTS (SELECT 1 FROM AgentTaskDeliveryAttempts a JOIN AgentReplyTasks t ON t.ID = a.TaskId
 WHERE a.ConnectionSessionId = NEW.ID AND (t.AgentParticipantId != NEW.ParticipantId OR t.MeetingId != NEW.MeetingId))
BEGIN SELECT RAISE(ABORT, 'Agent connection owner mismatch'); END;
CREATE TRIGGER agent_retry_owner_insert BEFORE INSERT ON AgentRecoveryOperations
WHEN NEW.TaskId IS NOT NULL AND EXISTS (SELECT 1 FROM AgentReplyTasks t WHERE t.ID = NEW.TaskId AND t.AgentParticipantId != NEW.ParticipantId)
BEGIN SELECT RAISE(ABORT, 'Agent retry receipt owner mismatch'); END;
CREATE TRIGGER agent_retry_owner_update BEFORE UPDATE OF TaskId, ParticipantId ON AgentRecoveryOperations
WHEN NEW.TaskId IS NOT NULL AND EXISTS (SELECT 1 FROM AgentReplyTasks t WHERE t.ID = NEW.TaskId AND t.AgentParticipantId != NEW.ParticipantId)
BEGIN SELECT RAISE(ABORT, 'Agent retry receipt owner mismatch'); END;
CREATE TEMP TABLE recovery_foreign_key_audit (Violation INTEGER CHECK (Violation = 0));
INSERT INTO recovery_foreign_key_audit SELECT count(*) FROM pragma_foreign_key_check;
DROP TABLE recovery_foreign_key_audit;
PRAGMA defer_foreign_keys = OFF;
""";
    private const string DowngradeSql = """
PRAGMA defer_foreign_keys = ON;
CREATE TEMP TABLE recovery_downgrade_audit (Violations INTEGER CHECK (Violations = 0));
INSERT INTO recovery_downgrade_audit
SELECT count(*) FROM AgentConnectionSessions c WHERE c.ReliabilityMode != 'legacy_v1'
 OR NOT EXISTS (SELECT 1 FROM AgentInitialContextDeliveries i WHERE i.ConnectionSessionId = c.ID);
DROP TABLE recovery_downgrade_audit;
DROP TRIGGER agent_attempt_owner_insert;
DROP TRIGGER agent_attempt_owner_update;
DROP TRIGGER agent_task_owner_update;
DROP TRIGGER agent_connection_owner_update;
DROP TRIGGER agent_retry_owner_insert;
DROP TRIGGER agent_retry_owner_update;
CREATE TEMP TABLE recovery_downgrade_connections AS
SELECT c.*, i.SnapshotSequence AS InitialSnapshotSequence, i.State AS InitialContextState,
 i.ConfirmedAtUtc AS InitialContextConfirmedAtUtc
FROM AgentConnectionSessions c JOIN AgentInitialContextDeliveries i ON i.ConnectionSessionId = c.ID;
DROP TABLE "AgentInitialContextDeliveries";

DROP TABLE "AgentRecoveryOperations";

DROP INDEX "IX_AgentTaskDeliveryAttempts_ReplacementAttemptId_TaskId_MeetingId";

DROP INDEX "IX_AgentTaskDeliveryAttempts_State_ConfirmationDeadlineUtc";

DROP INDEX "IX_AgentReplyTasks_CurrentAttemptId_ID_MeetingId";

DROP INDEX "IX_AgentReplyTasks_State_NextRetryAtUtc_TriggerSequence";

DROP INDEX "IX_AgentContextCursors_CurrentConnectionId_MeetingId_ParticipantId_ConnectionGeneration";

DROP INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId";

DROP INDEX "IX_AgentConnectionSessions_State_LeaseExpiresAtUtc";

DROP INDEX "IX_AgentConnectionSessions_SupersededByConnectionId_MeetingId_ParticipantId";

CREATE UNIQUE INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId" ON "AgentConnectionSessions" ("MeetingId", "ParticipantId") WHERE State = 'Connected';

CREATE TABLE "ef_temp_AgentConnectionSessions" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentConnectionSessions" PRIMARY KEY,
    "ConnectedAtUtc" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "DeclaredContextCharacterLimit" INTEGER NOT NULL,
    "DisconnectedAtUtc" TEXT NULL,
    "InitialContextConfirmedAtUtc" TEXT NULL,
    "InitialContextState" TEXT NOT NULL,
    "InitialSnapshotSequence" INTEGER NOT NULL,
    "LastActivityAtUtc" TEXT NOT NULL,
    "McpTransportBinding" TEXT NOT NULL,
    "MeetingId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "State" TEXT NOT NULL,
    "Transport" TEXT NOT NULL,
    CONSTRAINT "AK_AgentConnectionSessions_ID_MeetingId" UNIQUE ("ID", "MeetingId"),
    CONSTRAINT "CK_AgentConnectionSessions_CharacterLimit" CHECK (DeclaredContextCharacterLimit > 0),
    CONSTRAINT "CK_AgentConnectionSessions_ContextConfirmation" CHECK ((InitialContextState = 'Prepared' AND InitialContextConfirmedAtUtc IS NULL) OR (InitialContextState = 'Confirmed' AND InitialContextConfirmedAtUtc IS NOT NULL)),
    CONSTRAINT "CK_AgentConnectionSessions_InitialContextState" CHECK (InitialContextState IN ('Prepared', 'Confirmed')),
    CONSTRAINT "CK_AgentConnectionSessions_InitialSnapshot" CHECK (InitialSnapshotSequence BETWEEN 0 AND 9007199254740991),
    CONSTRAINT "CK_AgentConnectionSessions_State" CHECK (State IN ('Connected', 'Disconnected')),
    CONSTRAINT "CK_AgentConnectionSessions_StateTimes" CHECK ((State = 'Connected' AND DisconnectedAtUtc IS NULL) OR (State = 'Disconnected' AND DisconnectedAtUtc IS NOT NULL)),
    CONSTRAINT "FK_AgentConnectionSessions_MeetingParticipants_ParticipantId_MeetingId" FOREIGN KEY ("ParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentConnectionSessions_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentConnectionSessions" ("ID", "ConnectedAtUtc", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DisconnectedAtUtc", "InitialContextConfirmedAtUtc", "InitialContextState", "InitialSnapshotSequence", "LastActivityAtUtc", "McpTransportBinding", "MeetingId", "ParticipantId", "State", "Transport")
SELECT "ID", "ConnectedAtUtc", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DisconnectedAtUtc", "InitialContextConfirmedAtUtc", "InitialContextState", "InitialSnapshotSequence", "LastActivityAtUtc", "McpTransportBinding", "MeetingId", "ParticipantId", "State", "Transport"
FROM recovery_downgrade_connections;
DROP TABLE recovery_downgrade_connections;

CREATE TABLE "ef_temp_AgentContextCursors" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentContextCursors" PRIMARY KEY,
    "AllocatedThroughSequence" INTEGER NOT NULL,
    "DeliveredThroughSequence" INTEGER NOT NULL,
    "MeetingId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "AK_AgentContextCursors_ID_MeetingId" UNIQUE ("ID", "MeetingId"),
    CONSTRAINT "CK_AgentContextCursors_Sequences" CHECK (DeliveredThroughSequence BETWEEN 0 AND 9007199254740991 AND AllocatedThroughSequence BETWEEN 0 AND 9007199254740991 AND DeliveredThroughSequence <= AllocatedThroughSequence),
    CONSTRAINT "FK_AgentContextCursors_MeetingParticipants_ParticipantId_MeetingId" FOREIGN KEY ("ParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentContextCursors_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentContextCursors" ("ID", "AllocatedThroughSequence", "DeliveredThroughSequence", "MeetingId", "ParticipantId", "UpdatedAtUtc")
SELECT "ID", "AllocatedThroughSequence", "DeliveredThroughSequence", "MeetingId", "ParticipantId", "UpdatedAtUtc"
FROM "AgentContextCursors";

CREATE TABLE "ef_temp_AgentReplyTasks" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentReplyTasks" PRIMARY KEY,
    "AgentParticipantId" TEXT NOT NULL,
    "CancelledAtUtc" TEXT NULL,
    "CandidateFromSequenceExclusive" INTEGER NOT NULL,
    "CandidateToSequenceInclusive" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "DeliveredAtUtc" TEXT NULL,
    "FailedAtUtc" TEXT NULL,
    "FailureCode" TEXT NULL,
    "MeetingId" TEXT NOT NULL,
    "ProcessingAtUtc" TEXT NULL,
    "RepliedAtUtc" TEXT NULL,
    "ReplyIdempotencyKey" TEXT NOT NULL,
    "ReplyMessageId" TEXT NULL,
    "SourceAuthorKind" TEXT NOT NULL,
    "SourceParticipantId" TEXT NULL,
    "State" TEXT NOT NULL,
    "TriggerMentionId" TEXT NOT NULL,
    "TriggerMessageId" TEXT NOT NULL,
    "TriggerSequence" INTEGER NOT NULL,
    CONSTRAINT "AK_AgentReplyTasks_ID_MeetingId" UNIQUE ("ID", "MeetingId"),
    CONSTRAINT "CK_AgentReplyTasks_CandidateRange" CHECK (CandidateFromSequenceExclusive BETWEEN 0 AND 9007199254740990 AND CandidateFromSequenceExclusive < CandidateToSequenceInclusive AND CandidateToSequenceInclusive = TriggerSequence AND TriggerSequence BETWEEN 1 AND 9007199254740991),
    CONSTRAINT "CK_AgentReplyTasks_Source" CHECK ((SourceAuthorKind = 'System' AND SourceParticipantId IS NULL) OR (SourceAuthorKind IN ('Human', 'Agent') AND SourceParticipantId IS NOT NULL)),
    CONSTRAINT "CK_AgentReplyTasks_State" CHECK (State IN ('Queued', 'Delivered', 'Processing', 'Replied', 'Failed', 'Cancelled')),
    CONSTRAINT "CK_AgentReplyTasks_Timestamps" CHECK ((State != 'Delivered' OR DeliveredAtUtc IS NOT NULL) AND (State != 'Processing' OR ProcessingAtUtc IS NOT NULL) AND (State != 'Replied' OR (RepliedAtUtc IS NOT NULL AND ReplyMessageId IS NOT NULL)) AND (State != 'Failed' OR (FailedAtUtc IS NOT NULL AND FailureCode IS NOT NULL)) AND (State != 'Cancelled' OR CancelledAtUtc IS NOT NULL)),
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_ReplyMessageId_MeetingId" FOREIGN KEY ("ReplyMessageId", "MeetingId") REFERENCES "MeetingMessages" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_TriggerMessageId_MeetingId_TriggerSequence" FOREIGN KEY ("TriggerMessageId", "MeetingId", "TriggerSequence") REFERENCES "MeetingMessages" ("ID", "MeetingId", "Sequence") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingParticipants_AgentParticipantId_MeetingId" FOREIGN KEY ("AgentParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingParticipants_SourceParticipantId_MeetingId" FOREIGN KEY ("SourceParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MessageMentions_TriggerMentionId_MeetingId" FOREIGN KEY ("TriggerMentionId", "MeetingId") REFERENCES "MessageMentions" ("ID", "MeetingId") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentReplyTasks" ("ID", "AgentParticipantId", "CancelledAtUtc", "CandidateFromSequenceExclusive", "CandidateToSequenceInclusive", "CreatedAtUtc", "DeliveredAtUtc", "FailedAtUtc", "FailureCode", "MeetingId", "ProcessingAtUtc", "RepliedAtUtc", "ReplyIdempotencyKey", "ReplyMessageId", "SourceAuthorKind", "SourceParticipantId", "State", "TriggerMentionId", "TriggerMessageId", "TriggerSequence")
SELECT "ID", "AgentParticipantId", "CancelledAtUtc", "CandidateFromSequenceExclusive", "CandidateToSequenceInclusive", "CreatedAtUtc", "DeliveredAtUtc", "FailedAtUtc", "FailureCode", "MeetingId", "ProcessingAtUtc", "RepliedAtUtc", "ReplyIdempotencyKey", "ReplyMessageId", "SourceAuthorKind", "SourceParticipantId", "State", "TriggerMentionId", "TriggerMessageId", "TriggerSequence"
FROM "AgentReplyTasks";

CREATE TABLE "ef_temp_AgentTaskDeliveryAttempts" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentTaskDeliveryAttempts" PRIMARY KEY,
    "CandidateFirstSequence" INTEGER NOT NULL,
    "CandidateLastSequence" INTEGER NOT NULL,
    "ConnectionSessionId" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "DeclaredContextCharacterLimit" INTEGER NOT NULL,
    "DeliveredAtUtc" TEXT NULL,
    "DeliveredCharacterCount" INTEGER NOT NULL,
    "DeliveredFirstSequence" INTEGER NULL,
    "DeliveredLastSequence" INTEGER NULL,
    "MeetingId" TEXT NOT NULL,
    "OverLimit" INTEGER NOT NULL,
    "State" TEXT NOT NULL,
    "TaskId" TEXT NOT NULL,
    "Truncated" INTEGER NOT NULL,
    "TruncatedMessageCount" INTEGER NOT NULL,
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_CandidateRange" CHECK (CandidateFirstSequence BETWEEN 1 AND 9007199254740991 AND CandidateFirstSequence <= CandidateLastSequence AND CandidateLastSequence <= 9007199254740991),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_CharacterCounts" CHECK (DeclaredContextCharacterLimit > 0 AND DeliveredCharacterCount >= 0 AND TruncatedMessageCount >= 0 AND ((Truncated = 0 AND TruncatedMessageCount = 0) OR (Truncated = 1 AND TruncatedMessageCount > 0)) AND ((OverLimit = 0 AND DeliveredCharacterCount <= DeclaredContextCharacterLimit) OR (OverLimit = 1 AND DeliveredCharacterCount > DeclaredContextCharacterLimit))),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_DeliveredRange" CHECK ((State = 'Prepared' AND DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (State = 'Delivered' AND DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL) OR (State = 'Abandoned' AND ((DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL)))),
    CONSTRAINT "CK_AgentTaskDeliveryAttempts_State" CHECK (State IN ('Prepared', 'Delivered', 'Abandoned')),
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_AgentConnectionSessions_ConnectionSessionId_MeetingId" FOREIGN KEY ("ConnectionSessionId", "MeetingId") REFERENCES "AgentConnectionSessions" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_AgentReplyTasks_TaskId_MeetingId" FOREIGN KEY ("TaskId", "MeetingId") REFERENCES "AgentReplyTasks" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentTaskDeliveryAttempts_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentTaskDeliveryAttempts" ("ID", "CandidateFirstSequence", "CandidateLastSequence", "ConnectionSessionId", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DeliveredAtUtc", "DeliveredCharacterCount", "DeliveredFirstSequence", "DeliveredLastSequence", "MeetingId", "OverLimit", "State", "TaskId", "Truncated", "TruncatedMessageCount")
SELECT "ID", "CandidateFirstSequence", "CandidateLastSequence", "ConnectionSessionId", "CreatedAtUtc", "DeclaredContextCharacterLimit", "DeliveredAtUtc", "DeliveredCharacterCount", "DeliveredFirstSequence", "DeliveredLastSequence", "MeetingId", "OverLimit", "State", "TaskId", "Truncated", "TruncatedMessageCount"
FROM "AgentTaskDeliveryAttempts";



DROP TABLE "AgentConnectionSessions";

ALTER TABLE "ef_temp_AgentConnectionSessions" RENAME TO "AgentConnectionSessions";

DROP TABLE "AgentContextCursors";

ALTER TABLE "ef_temp_AgentContextCursors" RENAME TO "AgentContextCursors";

DROP TABLE "AgentReplyTasks";

ALTER TABLE "ef_temp_AgentReplyTasks" RENAME TO "AgentReplyTasks";

DROP TABLE "AgentTaskDeliveryAttempts";

ALTER TABLE "ef_temp_AgentTaskDeliveryAttempts" RENAME TO "AgentTaskDeliveryAttempts";



CREATE UNIQUE INDEX "IX_AgentConnectionSessions_McpTransportBinding" ON "AgentConnectionSessions" ("McpTransportBinding");

CREATE UNIQUE INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId" ON "AgentConnectionSessions" ("MeetingId", "ParticipantId") WHERE State = 'Connected';

CREATE INDEX "IX_AgentConnectionSessions_MeetingId_ParticipantId_State" ON "AgentConnectionSessions" ("MeetingId", "ParticipantId", "State");

CREATE INDEX "IX_AgentConnectionSessions_ParticipantId_MeetingId" ON "AgentConnectionSessions" ("ParticipantId", "MeetingId");

CREATE UNIQUE INDEX "IX_AgentContextCursors_MeetingId_ParticipantId" ON "AgentContextCursors" ("MeetingId", "ParticipantId");

CREATE INDEX "IX_AgentContextCursors_ParticipantId_MeetingId" ON "AgentContextCursors" ("ParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_AgentParticipantId_MeetingId" ON "AgentReplyTasks" ("AgentParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_State_TriggerSequence_ID" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "State", "TriggerSequence", "ID");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerMessageId" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "TriggerMessageId");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_ReplyIdempotencyKey" ON "AgentReplyTasks" ("ReplyIdempotencyKey");

CREATE INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId" ON "AgentReplyTasks" ("ReplyMessageId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_SourceParticipantId_MeetingId" ON "AgentReplyTasks" ("SourceParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_TriggerMentionId_MeetingId" ON "AgentReplyTasks" ("TriggerMentionId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_TriggerMessageId_MeetingId_TriggerSequence" ON "AgentReplyTasks" ("TriggerMessageId", "MeetingId", "TriggerSequence");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_ConnectionSessionId_MeetingId" ON "AgentTaskDeliveryAttempts" ("ConnectionSessionId", "MeetingId");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_MeetingId_ConnectionSessionId_State" ON "AgentTaskDeliveryAttempts" ("MeetingId", "ConnectionSessionId", "State");

CREATE UNIQUE INDEX "IX_AgentTaskDeliveryAttempts_TaskId_ID" ON "AgentTaskDeliveryAttempts" ("TaskId", "ID");

CREATE INDEX "IX_AgentTaskDeliveryAttempts_TaskId_MeetingId" ON "AgentTaskDeliveryAttempts" ("TaskId", "MeetingId");


CREATE TEMP TABLE recovery_foreign_key_audit (Violation INTEGER CHECK (Violation = 0));
INSERT INTO recovery_foreign_key_audit SELECT count(*) FROM pragma_foreign_key_check;
DROP TABLE recovery_foreign_key_audit;
PRAGMA defer_foreign_keys = OFF;
""";
}
