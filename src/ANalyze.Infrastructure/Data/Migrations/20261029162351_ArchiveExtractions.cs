using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANalyze.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveExtractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchiveExtractions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArchiveId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseRelPath = table.Column<string>(type: "TEXT", nullable: false),
                    Strip = table.Column<string>(type: "TEXT", nullable: false),
                    FolderId = table.Column<long>(type: "INTEGER", nullable: true),
                    FolderRelPath = table.Column<string>(type: "TEXT", nullable: false),
                    Files = table.Column<int>(type: "INTEGER", nullable: false),
                    Matched = table.Column<int>(type: "INTEGER", nullable: false),
                    Missing = table.Column<int>(type: "INTEGER", nullable: false),
                    Smaller = table.Column<int>(type: "INTEGER", nullable: false),
                    Differs = table.Column<int>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MatchedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    FoundAt = table.Column<long>(type: "INTEGER", nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    VerifiedOk = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiedBad = table.Column<int>(type: "INTEGER", nullable: false),
                    Unverified = table.Column<int>(type: "INTEGER", nullable: false),
                    BadPaths = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveExtractions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveListings",
                columns: table => new
                {
                    EntryId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    MTime = table.Column<long>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    Streamed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ListedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Files = table.Column<int>(type: "INTEGER", nullable: false),
                    FileBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveListings", x => x.EntryId);
                });

            migrationBuilder.CreateTable(
                name: "ArchiveMembers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArchiveId = table.Column<long>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Crc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveExtractions_ArchiveId",
                table: "ArchiveExtractions",
                column: "ArchiveId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveExtractions_State_MatchedBytes",
                table: "ArchiveExtractions",
                columns: new[] { "State", "MatchedBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveMembers_ArchiveId",
                table: "ArchiveMembers",
                column: "ArchiveId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveExtractions");

            migrationBuilder.DropTable(
                name: "ArchiveListings");

            migrationBuilder.DropTable(
                name: "ArchiveMembers");
        }
    }
}
