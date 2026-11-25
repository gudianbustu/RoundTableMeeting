using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ExpandGroupMentionTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "IsGroup", table: "MessageMentions", type: "INTEGER", nullable: false, defaultValue: false);

            migrationBuilder.DropIndex(
                name: "IX_MessageMentions_MessageId_MeetingId_StartOffset",
                table: "MessageMentions");

            migrationBuilder.CreateIndex(
                name: "IX_MessageMentions_MessageId_MeetingId_StartOffset_TargetParticipantId",
                table: "MessageMentions",
                columns: new[] { "MessageId", "MeetingId", "StartOffset", "TargetParticipantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Earlier schemas can only represent individual mentions at a given offset.
            migrationBuilder.Sql("DELETE FROM MessageMentions WHERE IsGroup = 1");
            // Keep the table in place because existing audit triggers reference it.
            migrationBuilder.Sql("ALTER TABLE MessageMentions DROP COLUMN IsGroup");

            migrationBuilder.DropIndex(
                name: "IX_MessageMentions_MessageId_MeetingId_StartOffset_TargetParticipantId",
                table: "MessageMentions");

            migrationBuilder.CreateIndex(
                name: "IX_MessageMentions_MessageId_MeetingId_StartOffset",
                table: "MessageMentions",
                columns: new[] { "MessageId", "MeetingId", "StartOffset" },
                unique: true);
        }
    }
}
