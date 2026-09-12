using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations;

public partial class AddMultiAgentCollaboration : Migration
{
    // Audit, table rebuild and migration history must commit together.
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpgradeSql);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DowngradeSql);

    private const string UpgradeSql = """
PRAGMA defer_foreign_keys = ON;
CREATE TEMP TABLE collaboration_migration_audit (Problem TEXT);
CREATE TEMP TRIGGER collaboration_migration_abort BEFORE INSERT ON collaboration_migration_audit
BEGIN SELECT RAISE(ABORT, NEW.Problem); END;
INSERT INTO collaboration_migration_audit
SELECT 'Invalid collaboration source for task ' || t.ID FROM AgentReplyTasks t
WHERE t.SourceAuthorKind != 'Human' OR NOT EXISTS (
 SELECT 1 FROM MeetingMessages m JOIN MessageMentions n ON n.MessageId = m.ID AND n.MeetingId = m.MeetingId
 JOIN MeetingParticipants a ON a.ID = t.AgentParticipantId AND a.MeetingId = t.MeetingId
 WHERE m.ID = t.TriggerMessageId AND m.MeetingId = t.MeetingId AND m.Sequence = t.TriggerSequence
 AND m.AuthorKind = 'Human' AND m.AuthorParticipantId = t.SourceParticipantId
 AND n.ID = t.TriggerMentionId AND n.TargetParticipantId = t.AgentParticipantId AND a.ParticipantType = 'Agent');
INSERT INTO collaboration_migration_audit
SELECT 'Invalid collaboration reply for task ' || t.ID FROM AgentReplyTasks t
WHERE (t.State = 'Replied' AND t.ReplyMessageId IS NULL)
 OR (t.ReplyMessageId IS NOT NULL AND NOT EXISTS (
 SELECT 1 FROM MeetingMessages m WHERE m.ID = t.ReplyMessageId AND m.MeetingId = t.MeetingId
 AND m.AuthorKind = 'Agent' AND m.AuthorParticipantId = t.AgentParticipantId AND m.Sequence > t.TriggerSequence));
INSERT INTO collaboration_migration_audit
SELECT 'Ambiguous collaboration reply ' || ReplyMessageId FROM AgentReplyTasks
WHERE ReplyMessageId IS NOT NULL GROUP BY MeetingId, ReplyMessageId HAVING count(*) > 1;
DROP TRIGGER collaboration_migration_abort;
DROP TABLE collaboration_migration_audit;
DROP INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId";

ALTER TABLE "AgentReplyTasks" ADD "ChainId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE "AgentReplyTasks" ADD "ParentTaskId" TEXT NULL;

CREATE TABLE "AgentCollaborationBoundaries" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentCollaborationBoundaries" PRIMARY KEY,
    "MeetingId" TEXT NOT NULL,
    "AgentTriggerStartSequence" INTEGER NOT NULL,
    CONSTRAINT "CK_AgentCollaborationBoundaries_Sequence" CHECK (AgentTriggerStartSequence BETWEEN 0 AND 9007199254740991),
    CONSTRAINT "FK_AgentCollaborationBoundaries_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT
);

CREATE INDEX "IX_AgentReplyTasks_ChainId_MeetingId" ON "AgentReplyTasks" ("ChainId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_ParentTaskId_MeetingId" ON "AgentReplyTasks" ("ParentTaskId", "MeetingId");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId" ON "AgentReplyTasks" ("ReplyMessageId", "MeetingId") WHERE ReplyMessageId IS NOT NULL;

CREATE UNIQUE INDEX "IX_AgentCollaborationBoundaries_MeetingId" ON "AgentCollaborationBoundaries" ("MeetingId");

UPDATE AgentReplyTasks SET ChainId = TriggerMessageId;
INSERT INTO AgentCollaborationBoundaries (ID, MeetingId, AgentTriggerStartSequence)
SELECT m.ID, m.ID, coalesce((SELECT max(x.Sequence) FROM MeetingMessages x WHERE x.MeetingId = m.ID), 0) FROM Meetings m;

CREATE TABLE "ef_temp_AgentReplyTasks" (
    "ID" TEXT NOT NULL CONSTRAINT "PK_AgentReplyTasks" PRIMARY KEY,
    "AgentParticipantId" TEXT NOT NULL,
    "AutomaticRetryCount" INTEGER NOT NULL,
    "CancelledAtUtc" TEXT NULL,
    "CandidateFromSequenceExclusive" INTEGER NOT NULL,
    "CandidateToSequenceInclusive" INTEGER NOT NULL,
    "ChainId" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "CurrentAttemptId" TEXT NULL,
    "DeliveredAtUtc" TEXT NULL,
    "FailedAtUtc" TEXT NULL,
    "FailureCode" TEXT NULL,
    "MeetingId" TEXT NOT NULL,
    "NextRetryAtUtc" TEXT NULL,
    "ParentTaskId" TEXT NULL,
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
    CONSTRAINT "CK_AgentReplyTasks_Source" CHECK (SourceAuthorKind IN ('Human', 'Agent') AND SourceParticipantId IS NOT NULL),
    CONSTRAINT "CK_AgentReplyTasks_State" CHECK (State IN ('Queued', 'Delivered', 'Processing', 'Replied', 'Failed', 'Cancelled')),
    CONSTRAINT "CK_AgentReplyTasks_Timestamps" CHECK ((State != 'Delivered' OR DeliveredAtUtc IS NOT NULL) AND (State != 'Processing' OR ProcessingAtUtc IS NOT NULL) AND (State != 'Replied' OR (RepliedAtUtc IS NOT NULL AND ReplyMessageId IS NOT NULL)) AND (State != 'Failed' OR (FailedAtUtc IS NOT NULL AND FailureCode IS NOT NULL)) AND (State != 'Cancelled' OR CancelledAtUtc IS NOT NULL)),
    CONSTRAINT "CK_AgentTasks_Retry" CHECK (RetryCycle >= 0 AND AutomaticRetryCount >= 0 AND ((RetryExhausted = 0 AND StopReason IS NULL) OR (RetryExhausted = 1 AND StopReason IS NOT NULL AND StopReason IN ('AttemptsExhausted', 'PermanentFailure') AND NextRetryAtUtc IS NULL))),
    CONSTRAINT "CK_AgentTasks_RetryTime" CHECK (NextRetryAtUtc IS NULL OR (State = 'Failed' AND FailedAtUtc IS NOT NULL AND NextRetryAtUtc >= FailedAtUtc)),
    CONSTRAINT "FK_AgentReplyTasks_AgentReplyTasks_ParentTaskId_MeetingId" FOREIGN KEY ("ParentTaskId", "MeetingId") REFERENCES "AgentReplyTasks" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_AgentTaskDeliveryAttempts_CurrentAttemptId_ID_MeetingId" FOREIGN KEY ("CurrentAttemptId", "ID", "MeetingId") REFERENCES "AgentTaskDeliveryAttempts" ("ID", "TaskId", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_ChainId_MeetingId" FOREIGN KEY ("ChainId", "MeetingId") REFERENCES "MeetingMessages" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_ReplyMessageId_MeetingId" FOREIGN KEY ("ReplyMessageId", "MeetingId") REFERENCES "MeetingMessages" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingMessages_TriggerMessageId_MeetingId_TriggerSequence" FOREIGN KEY ("TriggerMessageId", "MeetingId", "TriggerSequence") REFERENCES "MeetingMessages" ("ID", "MeetingId", "Sequence") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingParticipants_AgentParticipantId_MeetingId" FOREIGN KEY ("AgentParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MeetingParticipants_SourceParticipantId_MeetingId" FOREIGN KEY ("SourceParticipantId", "MeetingId") REFERENCES "MeetingParticipants" ("ID", "MeetingId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_Meetings_MeetingId" FOREIGN KEY ("MeetingId") REFERENCES "Meetings" ("ID") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentReplyTasks_MessageMentions_TriggerMentionId_MeetingId" FOREIGN KEY ("TriggerMentionId", "MeetingId") REFERENCES "MessageMentions" ("ID", "MeetingId") ON DELETE RESTRICT
);

INSERT INTO "ef_temp_AgentReplyTasks" ("ID", "AgentParticipantId", "AutomaticRetryCount", "CancelledAtUtc", "CandidateFromSequenceExclusive", "CandidateToSequenceInclusive", "ChainId", "CreatedAtUtc", "CurrentAttemptId", "DeliveredAtUtc", "FailedAtUtc", "FailureCode", "MeetingId", "NextRetryAtUtc", "ParentTaskId", "ProcessingAtUtc", "RepliedAtUtc", "ReplyIdempotencyKey", "ReplyMessageId", "RetryCycle", "RetryExhausted", "SourceAuthorKind", "SourceParticipantId", "State", "StopReason", "TriggerMentionId", "TriggerMessageId", "TriggerSequence")
SELECT "ID", "AgentParticipantId", "AutomaticRetryCount", "CancelledAtUtc", "CandidateFromSequenceExclusive", "CandidateToSequenceInclusive", "ChainId", "CreatedAtUtc", "CurrentAttemptId", "DeliveredAtUtc", "FailedAtUtc", "FailureCode", "MeetingId", "NextRetryAtUtc", "ParentTaskId", "ProcessingAtUtc", "RepliedAtUtc", "ReplyIdempotencyKey", "ReplyMessageId", "RetryCycle", "RetryExhausted", "SourceAuthorKind", "SourceParticipantId", "State", "StopReason", "TriggerMentionId", "TriggerMessageId", "TriggerSequence"
FROM "AgentReplyTasks";



DROP TRIGGER agent_attempt_owner_insert;
DROP TRIGGER agent_attempt_owner_update;
DROP TRIGGER agent_task_owner_update;
DROP TRIGGER agent_connection_owner_update;
DROP TRIGGER agent_retry_owner_insert;
DROP TRIGGER agent_retry_owner_update;
DROP TABLE "AgentReplyTasks";

ALTER TABLE "ef_temp_AgentReplyTasks" RENAME TO "AgentReplyTasks";



CREATE INDEX "IX_AgentReplyTasks_AgentParticipantId_MeetingId" ON "AgentReplyTasks" ("AgentParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_ChainId_MeetingId" ON "AgentReplyTasks" ("ChainId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_CurrentAttemptId_ID_MeetingId" ON "AgentReplyTasks" ("CurrentAttemptId", "ID", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_State_TriggerSequence_ID" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "State", "TriggerSequence", "ID");

CREATE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerSequence" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "TriggerSequence");

CREATE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerSequence_ID" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "TriggerSequence", "ID") WHERE State <> 'Replied' AND State <> 'Cancelled';

CREATE UNIQUE INDEX "IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerMessageId" ON "AgentReplyTasks" ("MeetingId", "AgentParticipantId", "TriggerMessageId");

CREATE INDEX "IX_AgentReplyTasks_ParentTaskId_MeetingId" ON "AgentReplyTasks" ("ParentTaskId", "MeetingId");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_ReplyIdempotencyKey" ON "AgentReplyTasks" ("ReplyIdempotencyKey");

CREATE UNIQUE INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId" ON "AgentReplyTasks" ("ReplyMessageId", "MeetingId") WHERE ReplyMessageId IS NOT NULL;

CREATE INDEX "IX_AgentReplyTasks_SourceParticipantId_MeetingId" ON "AgentReplyTasks" ("SourceParticipantId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_State_NextRetryAtUtc_TriggerSequence" ON "AgentReplyTasks" ("State", "NextRetryAtUtc", "TriggerSequence");

CREATE INDEX "IX_AgentReplyTasks_TriggerMentionId_MeetingId" ON "AgentReplyTasks" ("TriggerMentionId", "MeetingId");

CREATE INDEX "IX_AgentReplyTasks_TriggerMessageId_MeetingId_TriggerSequence" ON "AgentReplyTasks" ("TriggerMessageId", "MeetingId", "TriggerSequence");



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

CREATE TRIGGER agent_collaboration_source_insert BEFORE INSERT ON AgentReplyTasks
WHEN NOT EXISTS (SELECT 1 FROM Meetings m JOIN AgentCollaborationBoundaries b ON b.MeetingId = m.ID
 WHERE m.ID = NEW.MeetingId AND m.LifecycleState = 'Active'
 AND (NEW.SourceAuthorKind = 'Human' OR NEW.TriggerSequence > b.AgentTriggerStartSequence))
 OR NOT EXISTS (SELECT 1 FROM MeetingMessages root WHERE root.ID = NEW.ChainId
 AND root.MeetingId = NEW.MeetingId AND root.AuthorKind = 'Human')
 OR NOT EXISTS (SELECT 1 FROM MeetingMessages triggerMessage JOIN MessageMentions n
 ON n.MessageId = triggerMessage.ID AND n.MeetingId = triggerMessage.MeetingId
 JOIN MeetingParticipants target ON target.ID = NEW.AgentParticipantId AND target.MeetingId = NEW.MeetingId
 WHERE triggerMessage.ID = NEW.TriggerMessageId AND triggerMessage.MeetingId = NEW.MeetingId
 AND triggerMessage.Sequence = NEW.TriggerSequence AND triggerMessage.AuthorKind = NEW.SourceAuthorKind
 AND triggerMessage.AuthorParticipantId = NEW.SourceParticipantId AND n.ID = NEW.TriggerMentionId
 AND n.TargetParticipantId = NEW.AgentParticipantId AND target.ParticipantType = 'Agent')
 OR (NEW.SourceAuthorKind = 'Human' AND (NEW.ParentTaskId IS NOT NULL OR NEW.ChainId != NEW.TriggerMessageId))
 OR (NEW.SourceAuthorKind = 'Agent' AND NOT EXISTS (SELECT 1 FROM AgentReplyTasks parent
 JOIN MeetingMessages reply ON reply.ID = parent.ReplyMessageId AND reply.MeetingId = parent.MeetingId
 WHERE parent.ID = NEW.ParentTaskId AND parent.MeetingId = NEW.MeetingId AND parent.ChainId = NEW.ChainId
 AND parent.State = 'Replied' AND parent.ReplyMessageId = NEW.TriggerMessageId
 AND reply.AuthorKind = 'Agent' AND reply.AuthorParticipantId = parent.AgentParticipantId
 AND parent.TriggerSequence < NEW.TriggerSequence
 AND (parent.SourceAuthorKind = 'Human' AND parent.ParentTaskId IS NULL AND parent.ChainId = parent.TriggerMessageId
 OR parent.SourceAuthorKind = 'Agent' AND parent.ParentTaskId IS NOT NULL)))
BEGIN SELECT RAISE(ABORT, 'Invalid Agent collaboration source'); END;
CREATE TRIGGER agent_collaboration_source_update BEFORE UPDATE ON AgentReplyTasks
WHEN NEW.ID IS NOT OLD.ID OR NEW.MeetingId IS NOT OLD.MeetingId
 OR NEW.AgentParticipantId IS NOT OLD.AgentParticipantId OR NEW.ChainId IS NOT OLD.ChainId
 OR NEW.SourceAuthorKind IS NOT OLD.SourceAuthorKind OR NEW.SourceParticipantId IS NOT OLD.SourceParticipantId
 OR NEW.TriggerMessageId IS NOT OLD.TriggerMessageId OR NEW.TriggerMentionId IS NOT OLD.TriggerMentionId
 OR NEW.TriggerSequence IS NOT OLD.TriggerSequence
 OR (NEW.ParentTaskId IS NOT OLD.ParentTaskId AND NOT (NEW.ParentTaskId IS NULL
 AND EXISTS (SELECT 1 FROM Meetings m WHERE m.ID = OLD.MeetingId AND m.LifecycleState = 'Deleting')))
 OR (OLD.ReplyMessageId IS NOT NULL AND NEW.ReplyMessageId IS NOT OLD.ReplyMessageId)
BEGIN SELECT RAISE(ABORT, 'Agent collaboration source is immutable'); END;
CREATE TRIGGER agent_collaboration_reply_insert BEFORE INSERT ON AgentReplyTasks
WHEN NEW.ReplyMessageId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM MeetingMessages m
 WHERE m.ID = NEW.ReplyMessageId AND m.MeetingId = NEW.MeetingId AND m.AuthorKind = 'Agent'
 AND m.AuthorParticipantId = NEW.AgentParticipantId AND m.Sequence > NEW.TriggerSequence AND NEW.State = 'Replied')
BEGIN SELECT RAISE(ABORT, 'Invalid Agent collaboration reply'); END;
CREATE TRIGGER agent_collaboration_reply_update BEFORE UPDATE OF ReplyMessageId ON AgentReplyTasks
WHEN NEW.ReplyMessageId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM MeetingMessages m
 WHERE m.ID = NEW.ReplyMessageId AND m.MeetingId = NEW.MeetingId AND m.AuthorKind = 'Agent'
 AND m.AuthorParticipantId = NEW.AgentParticipantId AND m.Sequence > NEW.TriggerSequence AND NEW.State = 'Replied')
BEGIN SELECT RAISE(ABORT, 'Invalid Agent collaboration reply'); END;
CREATE TRIGGER agent_collaboration_boundary_update BEFORE UPDATE ON AgentCollaborationBoundaries
WHEN NEW.ID IS NOT OLD.ID OR NEW.MeetingId IS NOT OLD.MeetingId
 OR NEW.AgentTriggerStartSequence IS NOT OLD.AgentTriggerStartSequence
BEGIN SELECT RAISE(ABORT, 'Agent collaboration boundary is immutable'); END;
CREATE TEMP TABLE collaboration_foreign_key_audit (Violation INTEGER CHECK (Violation = 0));
INSERT INTO collaboration_foreign_key_audit SELECT count(*) FROM pragma_foreign_key_check;
DROP TABLE collaboration_foreign_key_audit;
PRAGMA defer_foreign_keys = OFF;
""";

    private const string DowngradeSql = """
PRAGMA defer_foreign_keys = ON;
CREATE TEMP TABLE collaboration_downgrade_audit (Violation INTEGER CHECK (Violation = 0));
INSERT INTO collaboration_downgrade_audit SELECT count(*) FROM AgentReplyTasks WHERE SourceAuthorKind != 'Human' OR ParentTaskId IS NOT NULL;
DROP TABLE collaboration_downgrade_audit;
DROP TRIGGER agent_collaboration_source_insert;
DROP TRIGGER agent_collaboration_source_update;
DROP TRIGGER agent_collaboration_reply_insert;
DROP TRIGGER agent_collaboration_reply_update;
DROP TRIGGER agent_collaboration_boundary_update;
DROP TABLE "AgentCollaborationBoundaries";

DROP INDEX "IX_AgentReplyTasks_ChainId_MeetingId";

DROP INDEX "IX_AgentReplyTasks_ParentTaskId_MeetingId";

DROP INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId";

CREATE INDEX "IX_AgentReplyTasks_ReplyMessageId_MeetingId" ON "AgentReplyTasks" ("ReplyMessageId", "MeetingId");

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



DROP TRIGGER agent_attempt_owner_insert;
DROP TRIGGER agent_attempt_owner_update;
DROP TRIGGER agent_task_owner_update;
DROP TRIGGER agent_connection_owner_update;
DROP TRIGGER agent_retry_owner_insert;
DROP TRIGGER agent_retry_owner_update;
DROP TABLE "AgentReplyTasks";

ALTER TABLE "ef_temp_AgentReplyTasks" RENAME TO "AgentReplyTasks";



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

CREATE TEMP TABLE collaboration_foreign_key_audit (Violation INTEGER CHECK (Violation = 0));
INSERT INTO collaboration_foreign_key_audit SELECT count(*) FROM pragma_foreign_key_check;
DROP TABLE collaboration_foreign_key_audit;
PRAGMA defer_foreign_keys = OFF;
""";
}
