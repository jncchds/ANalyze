using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANalyze.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupParentHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParentHash",
                table: "BackupFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RootId_HashedAt",
                table: "Entries",
                columns: new[] { "RootId", "HashedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entries_RootId_HashedAt",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ParentHash",
                table: "BackupFiles");
        }
    }
}
