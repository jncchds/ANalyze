using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANalyze.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CorruptAt",
                table: "Entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HashedAt",
                table: "Entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackupFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    IsDir = table.Column<bool>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalRootId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalRelPath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentSize = table.Column<long>(type: "INTEGER", nullable: true),
                    Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Children = table.Column<int>(type: "INTEGER", nullable: false),
                    SyncedChildren = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_EntryId",
                table: "BackupFiles",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_IsDir_State",
                table: "BackupFiles",
                columns: new[] { "IsDir", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_LocalRootId_LocalRelPath",
                table: "BackupFiles",
                columns: new[] { "LocalRootId", "LocalRelPath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupFiles");

            migrationBuilder.DropColumn(
                name: "CorruptAt",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "HashedAt",
                table: "Entries");
        }
    }
}
