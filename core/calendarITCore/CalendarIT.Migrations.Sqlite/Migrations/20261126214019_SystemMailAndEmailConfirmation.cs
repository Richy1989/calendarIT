using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SystemMailAndEmailConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ViaSystem",
                table: "OutboxMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Internal invitations now reach only confirmed addresses. Accounts that exist
            // before this upgrade were let in by the operator under the old rules, and their
            // owners already get invitations delivered — keep it that way: count them confirmed.
            // Only new sign-ups have to prove their address.
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"EmailConfirmed\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ViaSystem",
                table: "OutboxMessages");
        }
    }
}
