using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConversionProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Profile supersedes the KeepOriginal flag, so it has to be filled FROM that column before the
            // column goes: add, carry the data across, then drop. The scaffolder dropped it first, which
            // would have quietly re-encoded every video whose uploader had opted out of exactly that.
            migrationBuilder.AddColumn<string>(
                name: "Profile",
                table: "MediaItem",
                type: "TEXT",
                nullable: false,
                defaultValue: "Best");

            migrationBuilder.AddColumn<string>(
                name: "WebCodec",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HqFileName",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HqCodecs",
                table: "MediaItem",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultProfile",
                table: "Bucket",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"MediaItem\" SET \"Profile\" = 'AsUploaded' WHERE \"KeepOriginal\" = 1;");

            migrationBuilder.DropColumn(
                name: "KeepOriginal",
                table: "MediaItem");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaItem_HqCodecsNeedsFile",
                table: "MediaItem",
                sql: "\"HqCodecs\" IS NULL OR \"HqFileName\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaItem_HqCodecsNeedsFile",
                table: "MediaItem");

            // The same in reverse, so the opt-out survives a rollback: KeepOriginal is back and filled
            // before Profile goes. The three converting profiles all collapse to false, which is exactly
            // what the old flag meant.
            migrationBuilder.AddColumn<bool>(
                name: "KeepOriginal",
                table: "MediaItem",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE \"MediaItem\" SET \"KeepOriginal\" = 1 WHERE \"Profile\" = 'AsUploaded';");

            migrationBuilder.DropColumn(
                name: "Profile",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "WebCodec",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "HqFileName",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "HqCodecs",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "DefaultProfile",
                table: "Bucket");
        }
    }
}
