using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CalendarSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastFetchError",
                table: "Calendars",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFetchedUtc",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextFetchUtc",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RefreshMinutes",
                table: "Calendars",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceETag",
                table: "Calendars",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLastModified",
                table: "Calendars",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "Calendars",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_NextFetchUtc",
                table: "Calendars",
                column: "NextFetchUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Calendars_NextFetchUtc",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "LastFetchError",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "LastFetchedUtc",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "NextFetchUtc",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "RefreshMinutes",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SourceETag",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SourceLastModified",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "Calendars");
        }
    }
}
