using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class HlsRenditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HlsCodecs",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HlsHqCodecs",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HlsWebStem",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HlsCodecs",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "HlsHqCodecs",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "HlsWebStem",
                table: "MediaItem");
        }
    }
}
