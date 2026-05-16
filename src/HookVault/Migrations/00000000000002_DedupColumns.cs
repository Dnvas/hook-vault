using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HookVault.Migrations
{
    /// <inheritdoc />
    public partial class DedupColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodyHash",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderEventId",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_BodyHash",
                table: "Events",
                column: "BodyHash");

            migrationBuilder.CreateIndex(
                name: "IX_Events_ProviderEventId",
                table: "Events",
                column: "ProviderEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_BodyHash",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_ProviderEventId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "BodyHash",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ProviderEventId",
                table: "Events");
        }
    }
}
