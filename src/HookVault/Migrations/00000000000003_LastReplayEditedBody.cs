using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HookVault.Migrations
{
    /// <inheritdoc />
    public partial class LastReplayEditedBody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LastReplayWithEditedBody",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReplayWithEditedBody",
                table: "Events");
        }
    }
}
