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
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // Body: text -> bytea. UTF-8 round-trip is lossless because the SQLite
                // path stored UTF-8 byte content as TEXT; Postgres did the same via the
                // default text encoding.
                migrationBuilder.Sql("""
                    ALTER TABLE "Events"
                    ALTER COLUMN "Body" TYPE bytea
                    USING convert_to("Body", 'UTF8');
                """);

                // Headers: rewrite {"k":"v"} -> {"k":["v"]} in place. Headers stays text;
                // only its JSON shape changes. We parse with ::jsonb, build new value
                // arrays, then cast the result back to text for storage. Single-element
                // wrapping is correct for every row written prior to this migration —
                // no code path produces multi-value headers.
                migrationBuilder.Sql("""
                    UPDATE "Events"
                    SET "Headers" = (
                        SELECT jsonb_object_agg(key, jsonb_build_array(value))::text
                        FROM jsonb_each_text("Headers"::jsonb)
                    )
                    WHERE "Headers" IS NOT NULL AND "Headers" <> '' AND "Headers" <> '{}';
                """);

                return;
            }

            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                throw new NotSupportedException(
                    $"BytesBodyAndArrayHeaders does not support provider '{migrationBuilder.ActiveProvider}'. " +
                    "Supported providers: Microsoft.EntityFrameworkCore.Sqlite, " +
                    "Npgsql.EntityFrameworkCore.PostgreSQL.");

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
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // Reverse order of Up: flatten headers first, then revert body type.
                // Flatten {"k":["v"]} -> {"k":"v"} (takes the first element; safe because
                // the up-migration always wrapped in a single-element array and no caller
                // introduces multi-value headers on the old shape).
                migrationBuilder.Sql("""
                    UPDATE "Events"
                    SET "Headers" = (
                        SELECT jsonb_object_agg(key, value -> 0)::text
                        FROM jsonb_each("Headers"::jsonb)
                    )
                    WHERE "Headers" IS NOT NULL AND "Headers" <> '' AND "Headers" <> '{}';
                """);

                migrationBuilder.Sql("""
                    ALTER TABLE "Events"
                    ALTER COLUMN "Body" TYPE text
                    USING convert_from("Body", 'UTF8');
                """);

                return;
            }

            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                throw new NotSupportedException(
                    $"BytesBodyAndArrayHeaders does not support provider '{migrationBuilder.ActiveProvider}'. " +
                    "Supported providers: Microsoft.EntityFrameworkCore.Sqlite, " +
                    "Npgsql.EntityFrameworkCore.PostgreSQL.");

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
