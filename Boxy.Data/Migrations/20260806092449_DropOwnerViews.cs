using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropOwnerViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Owner previews were briefly logged (badged "you"); they're out of the log again, so the
            // rows go with the column.
            migrationBuilder.Sql("DELETE FROM MediaView WHERE IsOwner = 1;");

            migrationBuilder.DropColumn(
                name: "IsOwner",
                table: "MediaView");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                table: "MediaView",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
