using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data safety BEFORE the schema tightens: SQLite rebuilds each altered table and copies its
            // rows, so every row must already satisfy the new NOT NULL / CHECK rules or the copy fails.
            // Backfill owners so OwnerId can become required: adopt any owner-less box under the first
            // admin, give each drop-off its box's owner, then adopt any remaining owner-less share.
            migrationBuilder.Sql(
                "UPDATE \"Bucket\" SET \"OwnerId\" = (SELECT \"Id\" FROM \"User\" WHERE \"Role\" = 'Admin' ORDER BY \"Id\" LIMIT 1) WHERE \"OwnerId\" IS NULL;");
            migrationBuilder.Sql(
                "UPDATE \"MediaItem\" SET \"OwnerId\" = (SELECT b.\"OwnerId\" FROM \"Bucket\" b WHERE b.\"Id\" = \"MediaItem\".\"BucketId\") WHERE \"OwnerId\" IS NULL AND \"BucketId\" IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE \"MediaItem\" SET \"OwnerId\" = (SELECT \"Id\" FROM \"User\" WHERE \"Role\" = 'Admin' ORDER BY \"Id\" LIMIT 1) WHERE \"OwnerId\" IS NULL;");
            // Clear the two now-illegal states so their CHECK constraints hold on existing rows.
            migrationBuilder.Sql(
                "UPDATE \"MediaItem\" SET \"Published\" = 0 WHERE \"BucketId\" IS NOT NULL AND \"Published\" = 1;");
            migrationBuilder.Sql(
                "UPDATE \"MediaItem\" SET \"MaxDownloads\" = NULL WHERE \"MaxDownloads\" IS NOT NULL AND \"AllowDownload\" = 0;");

            migrationBuilder.DropIndex(
                name: "IX_MediaItem_CustomSlug",
                table: "MediaItem");

            migrationBuilder.DropColumn(
                name: "IsWebPlayable",
                table: "MediaItem");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "User",
                type: "TEXT",
                nullable: true,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "User",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "MediaItem",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "OwnerId",
                table: "MediaItem",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CustomSlug",
                table: "MediaItem",
                type: "TEXT",
                nullable: true,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Bucket",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "OwnerId",
                table: "Bucket",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItem_OwnerId_CustomSlug",
                table: "MediaItem",
                columns: new[] { "OwnerId", "CustomSlug" },
                unique: true,
                filter: "\"CustomSlug\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaItem_MaxDownloadsNeedsAllow",
                table: "MediaItem",
                sql: "\"MaxDownloads\" IS NULL OR \"AllowDownload\" = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaItem_PublishedIsShare",
                table: "MediaItem",
                sql: "\"Published\" = 0 OR \"BucketId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItem_OwnerId_CustomSlug",
                table: "MediaItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaItem_MaxDownloadsNeedsAllow",
                table: "MediaItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaItem_PublishedIsShare",
                table: "MediaItem");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "User",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "User",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "MediaItem",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<int>(
                name: "OwnerId",
                table: "MediaItem",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "CustomSlug",
                table: "MediaItem",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldCollation: "NOCASE");

            migrationBuilder.AddColumn<bool>(
                name: "IsWebPlayable",
                table: "MediaItem",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Bucket",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<int>(
                name: "OwnerId",
                table: "Bucket",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItem_CustomSlug",
                table: "MediaItem",
                column: "CustomSlug");
        }
    }
}
