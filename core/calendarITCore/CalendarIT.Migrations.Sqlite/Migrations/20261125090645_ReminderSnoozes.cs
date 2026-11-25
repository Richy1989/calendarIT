using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ReminderSnoozes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReminderSnoozes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReminderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurrenceStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderSnoozes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReminderSnoozes_Reminders_ReminderId",
                        column: x => x.ReminderId,
                        principalTable: "Reminders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReminderSnoozes_DueAtUtc",
                table: "ReminderSnoozes",
                column: "DueAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderSnoozes_ReminderId_OccurrenceStartUtc",
                table: "ReminderSnoozes",
                columns: new[] { "ReminderId", "OccurrenceStartUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderSnoozes");
        }
    }
}
