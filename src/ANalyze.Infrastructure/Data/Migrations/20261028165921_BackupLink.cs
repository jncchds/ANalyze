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
                    EntryId = table.Column<long>(type: "INTEGER", nullable: true),
                    LocalRootId = table.Column<int>(type: "INTEGER", nullable: true),
                    LocalRelPath = table.Column<string>(type: "TEXT", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    MTime = table.Column<long>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: true),
                    LocalSize = table.Column<long>(type: "INTEGER", nullable: true),
                    LocalMTime = table.Column<long>(type: "INTEGER", nullable: true),
                    LocalHash = table.Column<string>(type: "TEXT", nullable: true),
                    Side = table.Column<string>(type: "TEXT", nullable: true)
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
                name: "IX_BackupFiles_LocalRootId_LocalRelPath",
                table: "BackupFiles",
                columns: new[] { "LocalRootId", "LocalRelPath" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_State",
                table: "BackupFiles",
                column: "State");
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
