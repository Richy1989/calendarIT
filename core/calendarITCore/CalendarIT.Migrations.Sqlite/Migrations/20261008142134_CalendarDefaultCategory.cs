using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalendarIT.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CalendarDefaultCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultCategoryId",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_DefaultCategoryId",
                table: "Calendars",
                column: "DefaultCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Calendars_Categories_DefaultCategoryId",
                table: "Calendars",
                column: "DefaultCategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Calendars_Categories_DefaultCategoryId",
                table: "Calendars");

            migrationBuilder.DropIndex(
                name: "IX_Calendars_DefaultCategoryId",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "DefaultCategoryId",
                table: "Calendars");
        }
    }
}
