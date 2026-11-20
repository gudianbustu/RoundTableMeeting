using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddParticipantHistoryVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HiddenFromHistory",
                table: "MeetingParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep triggers on other tables that reference MeetingParticipants intact.
            migrationBuilder.Sql("ALTER TABLE MeetingParticipants DROP COLUMN HiddenFromHistory");
        }
    }
}
