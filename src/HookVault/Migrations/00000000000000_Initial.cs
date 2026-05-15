using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HookVault.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Headers = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SignatureHeader = table.Column<string>(type: "TEXT", nullable: true),
                    SignatureValid = table.Column<bool>(type: "INTEGER", nullable: true),
                    ValidationDetails = table.Column<string>(type: "TEXT", nullable: true),
                    ForwardUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ForwardedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ForwardStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    ForwardError = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ReplayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReplayAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Provider",
                table: "Events",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_Events_ReceivedAt",
                table: "Events",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Status",
                table: "Events",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
