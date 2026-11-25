using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class TaskRelatedTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RelatedToUid",
                table: "Tasks",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            // Subtasks made before this column existed: their RELATED-TO is their parent's UID.
            migrationBuilder.Sql(
                "UPDATE \"Tasks\" SET \"RelatedToUid\" = (SELECT p.\"Uid\" FROM \"Tasks\" p WHERE p.\"Id\" = \"Tasks\".\"ParentId\") " +
                "WHERE \"ParentId\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelatedToUid",
                table: "Tasks");
        }
    }
}
