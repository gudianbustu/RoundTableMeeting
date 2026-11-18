using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFreeDiscussionTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Capture the participant-visible boundary inside the same write as admission.
            migrationBuilder.Sql("""
                CREATE TABLE AgentInboxStarts (
                    ParticipantId TEXT PRIMARY KEY REFERENCES MeetingParticipants(ID) ON DELETE CASCADE,
                    MeetingId TEXT NOT NULL, StartSequence INTEGER NOT NULL);
                INSERT INTO AgentInboxStarts
                    SELECT p.ID, p.MeetingId, COALESCE((
                        SELECT MAX(v.Sequence) FROM ParticipantMessageViews v
                        JOIN MeetingMessages m ON m.ID = v.MessageId
                        WHERE v.ParticipantId = p.ID AND m.CreatedAtUtc < p.JoinedAtUtc), 0)
                    FROM MeetingParticipants p WHERE p.ParticipantType = 'Agent';
                CREATE TRIGGER agent_inbox_admission AFTER INSERT ON MeetingParticipants
                    WHEN NEW.ParticipantType = 'Agent'
                    BEGIN
                        INSERT INTO AgentInboxStarts VALUES (NEW.ID, NEW.MeetingId,
                            (SELECT COUNT(*) FROM MeetingMessages WHERE MeetingId = NEW.MeetingId AND Visibility = 'public'));
                    END;
                CREATE TRIGGER agent_inbox_reentry AFTER UPDATE OF MembershipState ON MeetingParticipants
                    WHEN NEW.ParticipantType = 'Agent' AND OLD.MembershipState = 'Left' AND NEW.MembershipState = 'Joined'
                    BEGIN
                        UPDATE AgentInboxStarts SET StartSequence = COALESCE((
                            SELECT HighWatermark FROM ParticipantMessageCounters WHERE ParticipantId = NEW.ID), 0)
                            WHERE ParticipantId = NEW.ID;
                    END;
                """);
            migrationBuilder.CreateTable(
                name: "MeetingDiscussionTurns",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TopicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Optional = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingDiscussionTurns", x => x.ID);
                    table.ForeignKey(
                        name: "FK_MeetingDiscussionTurns_MeetingMessages_ID",
                        column: x => x.ID,
                        principalTable: "MeetingMessages",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO MeetingDiscussionTurns (ID, MeetingId, TopicId, Sequence, Optional)
                SELECT m.ID, m.MeetingId, m.ID, m.Sequence, 1 FROM MeetingMessages m
                WHERE m.Visibility = 'public' AND m.AuthorKind = 'Human'
                    AND NOT EXISTS (SELECT 1 FROM MessageMentions n WHERE n.MessageId = m.ID)
                    AND (SELECT c.FreeDiscussion FROM MeetingDiscussionChanges c WHERE c.MeetingId = m.MeetingId
                        AND c.Sequence < m.Sequence ORDER BY c.Sequence DESC LIMIT 1) = 1;
                """);
            migrationBuilder.CreateIndex(
                name: "IX_MeetingDiscussionTurns_MeetingId_Sequence",
                table: "MeetingDiscussionTurns",
                columns: new[] { "MeetingId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingDiscussionTurns_TopicId_Optional",
                table: "MeetingDiscussionTurns",
                columns: new[] { "TopicId", "Optional" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER agent_inbox_admission; DROP TRIGGER agent_inbox_reentry; DROP TABLE AgentInboxStarts;");
            migrationBuilder.DropTable(
                name: "MeetingDiscussionTurns");
        }
    }
}
