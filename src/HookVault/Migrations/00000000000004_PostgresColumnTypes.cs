using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HookVault.Migrations;

/// <summary>
/// Corrects column types on the Events table for Postgres. Earlier migrations
/// were generated against the SQLite provider and used <c>type: "INTEGER"</c>
/// uniformly; Postgres interprets that as <c>integer</c>, which mismatches the
/// model's <c>bool</c> and <c>long</c> properties at write time. SQLite
/// tolerates the mismatch via dynamic typing; Postgres does not.
///
/// SQLite branch is a no-op: declared types do not constrain SQLite columns
/// and the live values are already correct.
/// </summary>
public partial class PostgresColumnTypes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            // Id is the primary key; drop the PK constraint before altering its type,
            // then recreate it. The text values are valid UUID strings so the cast
            // via ::uuid succeeds on any database created by this migration chain.
            migrationBuilder.Sql("""
                ALTER TABLE "Events" DROP CONSTRAINT "PK_Events";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "Id" TYPE uuid USING "Id"::uuid;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Events" ADD CONSTRAINT "PK_Events" PRIMARY KEY ("Id");
                """);

            // Drop the integer default on LastReplayWithEditedBody before casting; Postgres
            // cannot automatically coerce a DEFAULT 0 (integer literal) to boolean.
            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "LastReplayWithEditedBody" DROP DEFAULT;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "SignatureValid" TYPE boolean
                        USING CASE
                            WHEN "SignatureValid" = 0 THEN false
                            WHEN "SignatureValid" = 1 THEN true
                            ELSE NULL
                        END,
                    ALTER COLUMN "LastReplayWithEditedBody" TYPE boolean
                        USING CASE
                            WHEN "LastReplayWithEditedBody" = 0 THEN false
                            ELSE true
                        END,
                    ALTER COLUMN "ReceivedAt" TYPE bigint USING "ReceivedAt"::bigint,
                    ALTER COLUMN "ForwardedAt" TYPE bigint USING "ForwardedAt"::bigint,
                    ALTER COLUMN "LastReplayAt" TYPE bigint USING "LastReplayAt"::bigint;
                """);

            // Restore the boolean default.
            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "LastReplayWithEditedBody" SET DEFAULT false;
                """);
        }
    }

    /// <summary>
    /// Rollback path. <strong>Lossy</strong> for rows whose unix-second timestamps
    /// exceed Int32.MaxValue (2038-01-19T03:14:07Z): the bigint→integer cast
    /// silently truncates / overflows. The migration is reversible for any
    /// database created and rolled back before that date with no stored
    /// post-2038 timestamps; rolling back a production database with future-dated
    /// events would corrupt them. Operators rolling back should snapshot first.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            // Revert Id from uuid back to text.
            migrationBuilder.Sql("""
                ALTER TABLE "Events" DROP CONSTRAINT "PK_Events";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "Id" TYPE text USING "Id"::text;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Events" ADD CONSTRAINT "PK_Events" PRIMARY KEY ("Id");
                """);

            // Drop boolean default before reverting type.
            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "LastReplayWithEditedBody" DROP DEFAULT;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "SignatureValid" TYPE integer
                        USING CASE WHEN "SignatureValid" THEN 1 ELSE 0 END,
                    ALTER COLUMN "LastReplayWithEditedBody" TYPE integer
                        USING CASE WHEN "LastReplayWithEditedBody" THEN 1 ELSE 0 END,
                    ALTER COLUMN "ReceivedAt" TYPE integer USING "ReceivedAt"::integer,
                    ALTER COLUMN "ForwardedAt" TYPE integer USING "ForwardedAt"::integer,
                    ALTER COLUMN "LastReplayAt" TYPE integer USING "LastReplayAt"::integer;
                """);

            // Restore the integer default.
            migrationBuilder.Sql("""
                ALTER TABLE "Events"
                    ALTER COLUMN "LastReplayWithEditedBody" SET DEFAULT 0;
                """);
        }
    }
}
