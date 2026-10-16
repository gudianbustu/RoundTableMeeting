using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingMembershipLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing deletion receipts must also stop expiring; never revive an old creation intent.
            migrationBuilder.Sql("UPDATE MeetingCreationReceipts SET ExpiresAtUtc = NULL WHERE State = 'Deleted';");
            migrationBuilder.AddColumn<long>(
                name: "LeftThroughSequence",
                table: "MeetingParticipants",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MembershipRevision",
                table: "MeetingParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "MembershipState",
                table: "MeetingParticipants",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Joined");

            migrationBuilder.CreateTable(
                name: "MeetingDeletionOperations",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingDeletionOperations", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "MeetingLeaveOperations",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VisibleThroughSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    MembershipRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingLeaveOperations", x => x.ID);
                    table.ForeignKey(
                        name: "FK_MeetingLeaveOperations_MeetingParticipants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "MeetingParticipants",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeetingLeaveOperations_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeetingLifecycleProofs",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProofHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingLifecycleProofs", x => x.ID);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingDeletionOperations_MeetingId",
                table: "MeetingDeletionOperations",
                column: "MeetingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingLeaveOperations_MeetingId_ParticipantId_OperationId",
                table: "MeetingLeaveOperations",
                columns: new[] { "MeetingId", "ParticipantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingLeaveOperations_ParticipantId",
                table: "MeetingLeaveOperations",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingLifecycleProofs_MeetingId_ProofHash",
                table: "MeetingLifecycleProofs",
                columns: new[] { "MeetingId", "ProofHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Old binaries cannot honor these durable permissions or deletion proofs.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __MembershipDowngradeGuard (Allowed INTEGER CONSTRAINT CK_PreserveLifecycleFacts CHECK (Allowed = 1));
                INSERT INTO __MembershipDowngradeGuard SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM MeetingParticipants WHERE MembershipState = 'Left') OR
                    EXISTS (SELECT 1 FROM MeetingDeletionOperations) OR
                    EXISTS (SELECT 1 FROM MeetingLifecycleProofs) OR
                    EXISTS (SELECT 1 FROM MeetingLeaveOperations)
                    THEN 0 ELSE 1 END;
                DROP TABLE __MembershipDowngradeGuard;
                """);
            migrationBuilder.DropTable(
                name: "MeetingDeletionOperations");

            migrationBuilder.DropTable(
                name: "MeetingLeaveOperations");

            migrationBuilder.DropTable(
                name: "MeetingLifecycleProofs");

            // Native DROP COLUMN preserves the table while existing cross-table triggers reference it.
            migrationBuilder.Sql("ALTER TABLE MeetingParticipants DROP COLUMN LeftThroughSequence;");
            migrationBuilder.Sql("ALTER TABLE MeetingParticipants DROP COLUMN MembershipRevision;");
            migrationBuilder.Sql("ALTER TABLE MeetingParticipants DROP COLUMN MembershipState;");
        }
    }
}
