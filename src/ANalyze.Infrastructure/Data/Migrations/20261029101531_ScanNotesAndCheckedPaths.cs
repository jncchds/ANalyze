using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANalyze.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScanNotesAndCheckedPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckedPath",
                table: "PlexItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckedPath",
                table: "IntegrationItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OtherFilesystem",
                table: "Entries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckedPath",
                table: "PlexItems");

            migrationBuilder.DropColumn(
                name: "CheckedPath",
                table: "IntegrationItems");

            migrationBuilder.DropColumn(
                name: "OtherFilesystem",
                table: "Entries");
        }
    }
}
