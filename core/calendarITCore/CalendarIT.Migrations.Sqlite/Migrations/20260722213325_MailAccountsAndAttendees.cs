using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MailAccountsAndAttendees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Attendees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attendees_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailAccounts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImapHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ImapPort = table.Column<int>(type: "INTEGER", nullable: false),
                    ImapUseSsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAccounts", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_MailAccounts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId",
                table: "Attendees",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attendees");

            migrationBuilder.DropTable(
                name: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "Events");
        }
    }
}
