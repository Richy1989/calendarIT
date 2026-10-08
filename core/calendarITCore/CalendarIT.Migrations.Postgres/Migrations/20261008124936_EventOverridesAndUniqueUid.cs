using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class EventOverridesAndUniqueUid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new unique (CalendarId, Uid) index can't be created over existing duplicates
            // (concurrent CalDAV PUTs or imports could insert them before). Keep every row: the
            // oldest keeps its UID, later ones get a unique suffix — they stay visible and editable
            // in the web UI, and sync to clients as separate resources from then on.
            migrationBuilder.Sql("""
                UPDATE "Events" AS e
                SET "Uid" = LEFT(e."Uid", 200) || '-dup-' || REPLACE(e."Id"::text, '-', '')
                FROM (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "CalendarId", "Uid" ORDER BY "CreatedAt", "Id") AS rn
                    FROM "Events"
                ) AS d
                WHERE e."Id" = d."Id" AND d.rn > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Events_CalendarId",
                table: "Events");

            migrationBuilder.AddColumn<DateTime>(
                name: "RecurrenceIdUtc",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesMasterId",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CalendarId_Uid",
                table: "Events",
                columns: new[] { "CalendarId", "Uid" },
                unique: true,
                filter: "\"SeriesMasterId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SeriesMasterId_RecurrenceIdUtc",
                table: "Events",
                columns: new[] { "SeriesMasterId", "RecurrenceIdUtc" },
                unique: true,
                filter: "\"SeriesMasterId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Events_SeriesMasterId",
                table: "Events",
                column: "SeriesMasterId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_Events_SeriesMasterId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_CalendarId_Uid",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_SeriesMasterId_RecurrenceIdUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RecurrenceIdUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SeriesMasterId",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Events_CalendarId",
                table: "Events",
                column: "CalendarId");
        }
    }
}
