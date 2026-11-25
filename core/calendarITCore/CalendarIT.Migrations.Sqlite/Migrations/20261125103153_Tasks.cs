using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Tasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    DueUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    RRule = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Tasks_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Tasks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CalendarId_Status",
                table: "Tasks",
                columns: new[] { "CalendarId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CalendarId_Uid",
                table: "Tasks",
                columns: new[] { "CalendarId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ParentId",
                table: "Tasks",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
