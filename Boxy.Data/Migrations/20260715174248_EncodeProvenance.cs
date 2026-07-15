using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class EncodeProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EncodeCrf",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EncodeMs",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncodePreset",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EncodeToneMapped",
                table: "MediaItem",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "HqSizeBytes",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SourceIsHdr",
                table: "MediaItem",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebEncoder",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebHeight",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WebSizeBytes",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebWidth",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncodeCrf",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "EncodeMs",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "EncodePreset",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "EncodeToneMapped",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "HqSizeBytes",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "SourceIsHdr",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "WebEncoder",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "WebHeight",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "WebSizeBytes",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "WebWidth",
                table: "MediaItem");
        }
    }
}
