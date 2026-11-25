using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoundTableMeeting.Repository.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PreserveMeetingCreationInformation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreationInformationHash",
                table: "Meetings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreationInformationHash",
                table: "Meetings");
        }
    }
}
