using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MailFromAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromAddress",
                table: "MailAccounts",
                type: "TEXT",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromAddress",
                table: "MailAccounts");
        }
    }
}
