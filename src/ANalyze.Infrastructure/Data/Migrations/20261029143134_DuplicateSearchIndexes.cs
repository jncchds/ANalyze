using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANalyze.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DuplicateSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entries_IsDir_Size",
                table: "Entries");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_IsDir_Size_IsSymlink_Inode_Dev",
                table: "Entries",
                columns: new[] { "IsDir", "Size", "IsSymlink", "Inode", "Dev" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Unhashed",
                table: "Entries",
                column: "Size",
                filter: "IsDir = 0 AND IsSymlink = 0 AND FullHash IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entries_IsDir_Size_IsSymlink_Inode_Dev",
                table: "Entries");

            migrationBuilder.DropIndex(
                name: "IX_Entries_Unhashed",
                table: "Entries");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_IsDir_Size",
                table: "Entries",
                columns: new[] { "IsDir", "Size" });
        }
    }
}
