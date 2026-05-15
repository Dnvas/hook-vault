using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HookVault.Migrations
{
    /// <inheritdoc />
    public partial class BytesBodyAndArrayHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                throw new NotSupportedException(
                    "BytesBodyAndArrayHeaders migration currently only supports SQLite. " +
                    "Postgres users: please drop and recreate the database. Multi-provider " +
                    "migration support is tracked for a follow-up PR.");

            // SQLite can't ALTER COLUMN TYPE; recreate the table with the new schema.
            // Body TEXT -> BLOB. Headers stays TEXT (JSON shape changes but type doesn't).
            // The Headers values are wrapped in single-element arrays as part of the copy.

            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "Events_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Events_new" PRIMARY KEY,
                    "Provider" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "Headers" TEXT NOT NULL,
                    "Body" BLOB NOT NULL,
                    "ReceivedAt" INTEGER NOT NULL,
                    "SignatureHeader" TEXT NULL,
                    "SignatureValid" INTEGER NULL,
                    "ValidationDetails" TEXT NULL,
                    "ForwardUrl" TEXT NOT NULL,
                    "ForwardedAt" INTEGER NULL,
                    "ForwardStatusCode" INTEGER NULL,
                    "ForwardError" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "ReplayCount" INTEGER NOT NULL,
                    "LastReplayAt" INTEGER NULL,
                    "LastError" TEXT NULL
                );
            """);

            // Copy data. CAST(Body AS BLOB) interprets the existing TEXT as raw bytes.
            // Headers values get wrapped in single-element arrays so the new shape
            // (Dictionary<string, string[]>) is honoured. Uses SQLite's JSON1 extension.
            migrationBuilder.Sql("""
                INSERT INTO "Events_new" (
                    "Id", "Provider", "Path", "Headers", "Body", "ReceivedAt",
                    "SignatureHeader", "SignatureValid", "ValidationDetails",
                    "ForwardUrl", "ForwardedAt", "ForwardStatusCode", "ForwardError",
                    "Status", "ReplayCount", "LastReplayAt", "LastError"
                )
                SELECT
                    "Id", "Provider", "Path",
                    (
                        SELECT json_group_object(key, json_array(value))
                        FROM json_each("Events"."Headers")
                    ) AS "Headers",
                    CAST("Body" AS BLOB) AS "Body",
                    "ReceivedAt",
                    "SignatureHeader", "SignatureValid", "ValidationDetails",
                    "ForwardUrl", "ForwardedAt", "ForwardStatusCode", "ForwardError",
                    "Status", "ReplayCount", "LastReplayAt", "LastError"
                FROM "Events";
            """);

            migrationBuilder.Sql("DROP TABLE \"Events\";");
            migrationBuilder.Sql("ALTER TABLE \"Events_new\" RENAME TO \"Events\";");

            migrationBuilder.CreateIndex(name: "IX_Events_Provider", table: "Events", column: "Provider");
            migrationBuilder.CreateIndex(name: "IX_Events_Status", table: "Events", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_Events_ReceivedAt", table: "Events", column: "ReceivedAt");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                throw new NotSupportedException(
                    "BytesBodyAndArrayHeaders migration currently only supports SQLite.");

            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "Events_old" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Events_old" PRIMARY KEY,
                    "Provider" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "Headers" TEXT NOT NULL,
                    "Body" TEXT NOT NULL,
                    "ReceivedAt" INTEGER NOT NULL,
                    "SignatureHeader" TEXT NULL,
                    "SignatureValid" INTEGER NULL,
                    "ValidationDetails" TEXT NULL,
                    "ForwardUrl" TEXT NOT NULL,
                    "ForwardedAt" INTEGER NULL,
                    "ForwardStatusCode" INTEGER NULL,
                    "ForwardError" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "ReplayCount" INTEGER NOT NULL,
                    "LastReplayAt" INTEGER NULL,
                    "LastError" TEXT NULL
                );
            """);

            migrationBuilder.Sql("""
                INSERT INTO "Events_old"
                SELECT
                    "Id", "Provider", "Path",
                    (
                        SELECT json_group_object(key, json_extract(value, '$[0]'))
                        FROM json_each("Events"."Headers")
                    ) AS "Headers",
                    CAST("Body" AS TEXT) AS "Body",
                    "ReceivedAt", "SignatureHeader", "SignatureValid", "ValidationDetails",
                    "ForwardUrl", "ForwardedAt", "ForwardStatusCode", "ForwardError",
                    "Status", "ReplayCount", "LastReplayAt", "LastError"
                FROM "Events";
            """);

            migrationBuilder.Sql("DROP TABLE \"Events\";");
            migrationBuilder.Sql("ALTER TABLE \"Events_old\" RENAME TO \"Events\";");
            migrationBuilder.CreateIndex(name: "IX_Events_Provider", table: "Events", column: "Provider");
            migrationBuilder.CreateIndex(name: "IX_Events_Status", table: "Events", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_Events_ReceivedAt", table: "Events", column: "ReceivedAt");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }
    }
}
