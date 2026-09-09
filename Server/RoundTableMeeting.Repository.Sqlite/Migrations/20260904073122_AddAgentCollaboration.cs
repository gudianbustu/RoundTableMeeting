using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentCollaboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_MessageMentions_ID_MeetingId",
                table: "MessageMentions",
                columns: new[] { "ID", "MeetingId" });

            migrationBuilder.CreateTable(
                name: "AgentConnectionSessions",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Transport = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DeclaredContextCharacterLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    InitialSnapshotSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    InitialContextState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    InitialContextConfirmedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    McpTransportBinding = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConnectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastActivityAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DisconnectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConnectionSessions", x => x.ID);
                    table.UniqueConstraint("AK_AgentConnectionSessions_ID_MeetingId", x => new { x.ID, x.MeetingId });
                    table.CheckConstraint("CK_AgentConnectionSessions_CharacterLimit", "DeclaredContextCharacterLimit > 0");
                    table.CheckConstraint("CK_AgentConnectionSessions_ContextConfirmation", "(InitialContextState = 'Prepared' AND InitialContextConfirmedAtUtc IS NULL) OR (InitialContextState = 'Confirmed' AND InitialContextConfirmedAtUtc IS NOT NULL)");
                    table.CheckConstraint("CK_AgentConnectionSessions_InitialContextState", "InitialContextState IN ('Prepared', 'Confirmed')");
                    table.CheckConstraint("CK_AgentConnectionSessions_InitialSnapshot", "InitialSnapshotSequence BETWEEN 0 AND 9007199254740991");
                    table.CheckConstraint("CK_AgentConnectionSessions_State", "State IN ('Connected', 'Disconnected')");
                    table.CheckConstraint("CK_AgentConnectionSessions_StateTimes", "(State = 'Connected' AND DisconnectedAtUtc IS NULL) OR (State = 'Disconnected' AND DisconnectedAtUtc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AgentConnectionSessions_MeetingParticipants_ParticipantId_MeetingId",
                        columns: x => new { x.ParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentConnectionSessions_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentContextCursors",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveredThroughSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    AllocatedThroughSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentContextCursors", x => x.ID);
                    table.UniqueConstraint("AK_AgentContextCursors_ID_MeetingId", x => new { x.ID, x.MeetingId });
                    table.CheckConstraint("CK_AgentContextCursors_Sequences", "DeliveredThroughSequence BETWEEN 0 AND 9007199254740991 AND AllocatedThroughSequence BETWEEN 0 AND 9007199254740991 AND DeliveredThroughSequence <= AllocatedThroughSequence");
                    table.ForeignKey(
                        name: "FK_AgentContextCursors_MeetingParticipants_ParticipantId_MeetingId",
                        columns: x => new { x.ParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentContextCursors_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentReplyTasks",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TriggerMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TriggerSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    TriggerMentionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceAuthorKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceParticipantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CandidateFromSequenceExclusive = table.Column<long>(type: "INTEGER", nullable: false),
                    CandidateToSequenceInclusive = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReplyIdempotencyKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReplyMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ProcessingAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RepliedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReplyTasks", x => x.ID);
                    table.UniqueConstraint("AK_AgentReplyTasks_ID_MeetingId", x => new { x.ID, x.MeetingId });
                    table.CheckConstraint("CK_AgentReplyTasks_CandidateRange", "CandidateFromSequenceExclusive BETWEEN 0 AND 9007199254740990 AND CandidateFromSequenceExclusive < CandidateToSequenceInclusive AND CandidateToSequenceInclusive = TriggerSequence AND TriggerSequence BETWEEN 1 AND 9007199254740991");
                    table.CheckConstraint("CK_AgentReplyTasks_Source", "(SourceAuthorKind = 'System' AND SourceParticipantId IS NULL) OR (SourceAuthorKind IN ('Human', 'Agent') AND SourceParticipantId IS NOT NULL)");
                    table.CheckConstraint("CK_AgentReplyTasks_State", "State IN ('Queued', 'Delivered', 'Processing', 'Replied', 'Failed', 'Cancelled')");
                    table.CheckConstraint("CK_AgentReplyTasks_Timestamps", "(State != 'Delivered' OR DeliveredAtUtc IS NOT NULL) AND (State != 'Processing' OR ProcessingAtUtc IS NOT NULL) AND (State != 'Replied' OR (RepliedAtUtc IS NOT NULL AND ReplyMessageId IS NOT NULL)) AND (State != 'Failed' OR (FailedAtUtc IS NOT NULL AND FailureCode IS NOT NULL)) AND (State != 'Cancelled' OR CancelledAtUtc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AgentReplyTasks_MeetingMessages_ReplyMessageId_MeetingId",
                        columns: x => new { x.ReplyMessageId, x.MeetingId },
                        principalTable: "MeetingMessages",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentReplyTasks_MeetingMessages_TriggerMessageId_MeetingId_TriggerSequence",
                        columns: x => new { x.TriggerMessageId, x.MeetingId, x.TriggerSequence },
                        principalTable: "MeetingMessages",
                        principalColumns: new[] { "ID", "MeetingId", "Sequence" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentReplyTasks_MeetingParticipants_AgentParticipantId_MeetingId",
                        columns: x => new { x.AgentParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentReplyTasks_MeetingParticipants_SourceParticipantId_MeetingId",
                        columns: x => new { x.SourceParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentReplyTasks_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentReplyTasks_MessageMentions_TriggerMentionId_MeetingId",
                        columns: x => new { x.TriggerMentionId, x.MeetingId },
                        principalTable: "MessageMentions",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentTaskDeliveryAttempts",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CandidateFirstSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CandidateLastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredFirstSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    DeliveredLastSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    DeclaredContextCharacterLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveredCharacterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    OverLimit = table.Column<bool>(type: "INTEGER", nullable: false),
                    TruncatedMessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTaskDeliveryAttempts", x => x.ID);
                    table.CheckConstraint("CK_AgentTaskDeliveryAttempts_CandidateRange", "CandidateFirstSequence BETWEEN 1 AND 9007199254740991 AND CandidateFirstSequence <= CandidateLastSequence AND CandidateLastSequence <= 9007199254740991");
                    table.CheckConstraint("CK_AgentTaskDeliveryAttempts_CharacterCounts", "DeclaredContextCharacterLimit > 0 AND DeliveredCharacterCount >= 0 AND TruncatedMessageCount >= 0 AND ((Truncated = 0 AND TruncatedMessageCount = 0) OR (Truncated = 1 AND TruncatedMessageCount > 0)) AND ((OverLimit = 0 AND DeliveredCharacterCount <= DeclaredContextCharacterLimit) OR (OverLimit = 1 AND DeliveredCharacterCount > DeclaredContextCharacterLimit))");
                    table.CheckConstraint("CK_AgentTaskDeliveryAttempts_DeliveredRange", "(State = 'Prepared' AND DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (State = 'Delivered' AND DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL) OR (State = 'Abandoned' AND ((DeliveredFirstSequence IS NULL AND DeliveredLastSequence IS NULL AND DeliveredAtUtc IS NULL) OR (DeliveredFirstSequence BETWEEN CandidateFirstSequence AND CandidateLastSequence AND DeliveredLastSequence BETWEEN DeliveredFirstSequence AND CandidateLastSequence AND DeliveredAtUtc IS NOT NULL)))");
                    table.CheckConstraint("CK_AgentTaskDeliveryAttempts_State", "State IN ('Prepared', 'Delivered', 'Abandoned')");
                    table.ForeignKey(
                        name: "FK_AgentTaskDeliveryAttempts_AgentConnectionSessions_ConnectionSessionId_MeetingId",
                        columns: x => new { x.ConnectionSessionId, x.MeetingId },
                        principalTable: "AgentConnectionSessions",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentTaskDeliveryAttempts_AgentReplyTasks_TaskId_MeetingId",
                        columns: x => new { x.TaskId, x.MeetingId },
                        principalTable: "AgentReplyTasks",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentTaskDeliveryAttempts_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnectionSessions_McpTransportBinding",
                table: "AgentConnectionSessions",
                column: "McpTransportBinding",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnectionSessions_MeetingId_ParticipantId",
                table: "AgentConnectionSessions",
                columns: new[] { "MeetingId", "ParticipantId" },
                unique: true,
                filter: "State = 'Connected'");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnectionSessions_MeetingId_ParticipantId_State",
                table: "AgentConnectionSessions",
                columns: new[] { "MeetingId", "ParticipantId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConnectionSessions_ParticipantId_MeetingId",
                table: "AgentConnectionSessions",
                columns: new[] { "ParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentContextCursors_MeetingId_ParticipantId",
                table: "AgentContextCursors",
                columns: new[] { "MeetingId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentContextCursors_ParticipantId_MeetingId",
                table: "AgentContextCursors",
                columns: new[] { "ParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_AgentParticipantId_MeetingId",
                table: "AgentReplyTasks",
                columns: new[] { "AgentParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_MeetingId_AgentParticipantId_State_TriggerSequence_ID",
                table: "AgentReplyTasks",
                columns: new[] { "MeetingId", "AgentParticipantId", "State", "TriggerSequence", "ID" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_MeetingId_AgentParticipantId_TriggerMessageId",
                table: "AgentReplyTasks",
                columns: new[] { "MeetingId", "AgentParticipantId", "TriggerMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_ReplyIdempotencyKey",
                table: "AgentReplyTasks",
                column: "ReplyIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_ReplyMessageId_MeetingId",
                table: "AgentReplyTasks",
                columns: new[] { "ReplyMessageId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_SourceParticipantId_MeetingId",
                table: "AgentReplyTasks",
                columns: new[] { "SourceParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_TriggerMentionId_MeetingId",
                table: "AgentReplyTasks",
                columns: new[] { "TriggerMentionId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReplyTasks_TriggerMessageId_MeetingId_TriggerSequence",
                table: "AgentReplyTasks",
                columns: new[] { "TriggerMessageId", "MeetingId", "TriggerSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTaskDeliveryAttempts_ConnectionSessionId_MeetingId",
                table: "AgentTaskDeliveryAttempts",
                columns: new[] { "ConnectionSessionId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTaskDeliveryAttempts_MeetingId_ConnectionSessionId_State",
                table: "AgentTaskDeliveryAttempts",
                columns: new[] { "MeetingId", "ConnectionSessionId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTaskDeliveryAttempts_TaskId_ID",
                table: "AgentTaskDeliveryAttempts",
                columns: new[] { "TaskId", "ID" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTaskDeliveryAttempts_TaskId_MeetingId",
                table: "AgentTaskDeliveryAttempts",
                columns: new[] { "TaskId", "MeetingId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentContextCursors");

            migrationBuilder.DropTable(
                name: "AgentTaskDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "AgentConnectionSessions");

            migrationBuilder.DropTable(
                name: "AgentReplyTasks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MessageMentions_ID_MeetingId",
                table: "MessageMentions");
        }
    }
}
