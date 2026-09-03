using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_MeetingParticipants_ID_MeetingId",
                table: "MeetingParticipants",
                columns: new[] { "ID", "MeetingId" });

            migrationBuilder.CreateTable(
                name: "MeetingMessages",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorParticipantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AuthorKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AuthorDisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    ClientSubmissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingMessages", x => x.ID);
                    table.UniqueConstraint("AK_MeetingMessages_ID_MeetingId", x => new { x.ID, x.MeetingId });
                    table.UniqueConstraint("AK_MeetingMessages_ID_MeetingId_Sequence", x => new { x.ID, x.MeetingId, x.Sequence });
                    table.CheckConstraint("CK_MeetingMessages_AuthorKind", "AuthorKind IN ('Human', 'Agent', 'System')");
                    table.CheckConstraint("CK_MeetingMessages_AuthorParticipant", "(AuthorKind = 'System' AND AuthorParticipantId IS NULL) OR (AuthorKind IN ('Human', 'Agent') AND AuthorParticipantId IS NOT NULL)");
                    table.CheckConstraint("CK_MeetingMessages_Sequence", "Sequence BETWEEN 1 AND 9007199254740991");
                    table.ForeignKey(
                        name: "FK_MeetingMessages_MeetingParticipants_AuthorParticipantId_MeetingId",
                        columns: x => new { x.AuthorParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeetingMessages_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageAppendedEvents",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAppendedEvents", x => x.ID);
                    table.ForeignKey(
                        name: "FK_MessageAppendedEvents_MeetingMessages_MessageId_MeetingId_Sequence",
                        columns: x => new { x.MessageId, x.MeetingId, x.Sequence },
                        principalTable: "MeetingMessages",
                        principalColumns: new[] { "ID", "MeetingId", "Sequence" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageAppendedEvents_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageMentions",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    MeetingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetDisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StartOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    Length = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageMentions", x => x.ID);
                    table.CheckConstraint("CK_MessageMentions_Range", "StartOffset >= 0 AND Length > 1");
                    table.ForeignKey(
                        name: "FK_MessageMentions_MeetingMessages_MessageId_MeetingId",
                        columns: x => new { x.MessageId, x.MeetingId },
                        principalTable: "MeetingMessages",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageMentions_MeetingParticipants_TargetParticipantId_MeetingId",
                        columns: x => new { x.TargetParticipantId, x.MeetingId },
                        principalTable: "MeetingParticipants",
                        principalColumns: new[] { "ID", "MeetingId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageMentions_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageEventDeliveries",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConsumerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageEventDeliveries", x => x.ID);
                    table.CheckConstraint("CK_MessageEventDeliveries_State", "State IN ('Pending', 'Leased', 'Completed', 'Suspended')");
                    table.ForeignKey(
                        name: "FK_MessageEventDeliveries_MessageAppendedEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "MessageAppendedEvents",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMessages_AuthorParticipantId_MeetingId",
                table: "MeetingMessages",
                columns: new[] { "AuthorParticipantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMessages_MeetingId_AuthorKind_ClientSubmissionId",
                table: "MeetingMessages",
                columns: new[] { "MeetingId", "AuthorKind", "ClientSubmissionId" },
                unique: true,
                filter: "AuthorKind = 'System'");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMessages_MeetingId_AuthorParticipantId_ClientSubmissionId",
                table: "MeetingMessages",
                columns: new[] { "MeetingId", "AuthorParticipantId", "ClientSubmissionId" },
                unique: true,
                filter: "AuthorParticipantId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMessages_MeetingId_Sequence",
                table: "MeetingMessages",
                columns: new[] { "MeetingId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAppendedEvents_MeetingId",
                table: "MessageAppendedEvents",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageAppendedEvents_MessageId",
                table: "MessageAppendedEvents",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAppendedEvents_MessageId_MeetingId_Sequence",
                table: "MessageAppendedEvents",
                columns: new[] { "MessageId", "MeetingId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageEventDeliveries_EventId_ConsumerName",
                table: "MessageEventDeliveries",
                columns: new[] { "EventId", "ConsumerName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageMentions_MeetingId",
                table: "MessageMentions",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageMentions_MessageId_MeetingId_StartOffset",
                table: "MessageMentions",
                columns: new[] { "MessageId", "MeetingId", "StartOffset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageMentions_TargetParticipantId_MeetingId",
                table: "MessageMentions",
                columns: new[] { "TargetParticipantId", "MeetingId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageEventDeliveries");

            migrationBuilder.DropTable(
                name: "MessageMentions");

            migrationBuilder.DropTable(
                name: "MessageAppendedEvents");

            migrationBuilder.DropTable(
                name: "MeetingMessages");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MeetingParticipants_ID_MeetingId",
                table: "MeetingParticipants");
        }
    }
}
