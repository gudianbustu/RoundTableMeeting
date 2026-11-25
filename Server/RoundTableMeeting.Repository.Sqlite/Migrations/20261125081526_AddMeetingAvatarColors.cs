using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingAvatarColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AvatarColorAssignments",
                columns: table => new
                {
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvatarColorAssignments", x => new { x.ParticipantId, x.Url });
                    table.ForeignKey(
                        name: "FK_AvatarColorAssignments_MeetingParticipants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "MeetingParticipants",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvatarColorAssignments_MeetingId_Url_Slot",
                table: "AvatarColorAssignments",
                columns: new[] { "MeetingId", "Url", "Slot" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO AvatarColorAssignments (ParticipantId, MeetingId, Url, Slot)
                SELECT p.ID, p.MeetingId, a.Url,
                       ROW_NUMBER() OVER (PARTITION BY p.MeetingId, a.Url ORDER BY p.JoinedAtUtc, p.ID) - 1
                FROM MeetingParticipants p
                JOIN AvatarSelections a ON a.OwnerId = COALESCE(p.UserId, p.ID)
                    AND a.IsAccount = (p.UserId IS NOT NULL);
                """);

            foreach (var action in new[] { "INSERT", "UPDATE OF Url" })
            {
                var trigger = action == "INSERT" ? "AvatarSelectionColorInsert" : "AvatarSelectionColorUpdate";
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {trigger} AFTER {action} ON AvatarSelections
                    BEGIN
                        INSERT INTO AvatarColorAssignments (ParticipantId, MeetingId, Url, Slot)
                        SELECT p.ID, p.MeetingId, NEW.Url,
                               COALESCE((SELECT MAX(Slot) + 1 FROM AvatarColorAssignments
                                         WHERE MeetingId = p.MeetingId AND Url = NEW.Url), 0)
                        FROM MeetingParticipants p
                        WHERE COALESCE(p.UserId, p.ID) = NEW.OwnerId AND (p.UserId IS NOT NULL) = NEW.IsAccount
                        AND NOT EXISTS (SELECT 1 FROM AvatarColorAssignments
                                        WHERE ParticipantId = p.ID AND Url = NEW.Url);
                    END;
                    """);
            }

            migrationBuilder.Sql("""
                CREATE TRIGGER MeetingParticipantColorInsert AFTER INSERT ON MeetingParticipants
                BEGIN
                    INSERT INTO AvatarColorAssignments (ParticipantId, MeetingId, Url, Slot)
                    SELECT NEW.ID, NEW.MeetingId, a.Url,
                           COALESCE((SELECT MAX(Slot) + 1 FROM AvatarColorAssignments
                                     WHERE MeetingId = NEW.MeetingId AND Url = a.Url), 0)
                    FROM AvatarSelections a
                    WHERE a.OwnerId = COALESCE(NEW.UserId, NEW.ID) AND a.IsAccount = (NEW.UserId IS NOT NULL);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER MeetingParticipantColorInsert;
                DROP TRIGGER AvatarSelectionColorUpdate;
                DROP TRIGGER AvatarSelectionColorInsert;
                """);
            migrationBuilder.DropTable(
                name: "AvatarColorAssignments");
        }
    }
}
