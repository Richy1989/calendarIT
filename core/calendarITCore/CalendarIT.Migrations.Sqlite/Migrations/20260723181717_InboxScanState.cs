using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InboxScanState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ImapLastUid",
                table: "MailAccounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ImapUidValidity",
                table: "MailAccounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScanAt",
                table: "MailAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanIntervalMinutes",
                table: "MailAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImapLastUid",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "ImapUidValidity",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "LastScanAt",
                table: "MailAccounts");

            migrationBuilder.DropColumn(
                name: "ScanIntervalMinutes",
                table: "MailAccounts");
        }
    }
}
