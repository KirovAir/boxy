using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Boxy.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetireConversionProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Universal and FullSize are no longer offered. Items keep their profile and files; a box
            // default only decides future drop-offs, so it moves to the profile that replaces them.
            migrationBuilder.Sql(
                "UPDATE \"Bucket\" SET \"DefaultProfile\" = 'Best' WHERE \"DefaultProfile\" IN ('Universal', 'FullSize');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
