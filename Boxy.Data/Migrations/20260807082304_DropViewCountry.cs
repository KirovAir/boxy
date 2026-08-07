using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropViewCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Country",
                table: "MediaView");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "MediaView",
                type: "TEXT",
                nullable: true);
        }
    }
}
