using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANalyze.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Conflict = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifyHashes = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TrashPurgedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActionTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Op = table.Column<int>(type: "INTEGER", nullable: false),
                    Destination = table.Column<string>(type: "TEXT", nullable: true),
                    Conflict = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CacheKey = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Applied = table.Column<bool>(type: "INTEGER", nullable: false),
                    Dismissed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiRuleSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CategoryName = table.Column<string>(type: "TEXT", nullable: false),
                    Condition = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", nullable: true),
                    MatchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiRuleSuggestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Annotations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Annotations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    BuiltIn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirSizeHistory",
                columns: table => new
                {
                    ScanId = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: false),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelPath = table.Column<string>(type: "TEXT", nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirSizeHistory", x => new { x.ScanId, x.EntryId });
                });

            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<long>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    RelPath = table.Column<string>(type: "TEXT", nullable: false),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDir = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSymlink = table.Column<bool>(type: "INTEGER", nullable: false),
                    LinkTarget = table.Column<string>(type: "TEXT", nullable: true),
                    BrokenLink = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    AllocSize = table.Column<long>(type: "INTEGER", nullable: false),
                    MTime = table.Column<long>(type: "INTEGER", nullable: false),
                    ATime = table.Column<long>(type: "INTEGER", nullable: false),
                    CTime = table.Column<long>(type: "INTEGER", nullable: false),
                    Inode = table.Column<long>(type: "INTEGER", nullable: false),
                    Dev = table.Column<long>(type: "INTEGER", nullable: false),
                    NLink = table.Column<int>(type: "INTEGER", nullable: false),
                    Ext = table.Column<string>(type: "TEXT", nullable: true),
                    Mime = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Detected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    CategorySource = table.Column<int>(type: "INTEGER", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    QuickHash = table.Column<long>(type: "INTEGER", nullable: true),
                    FullHash = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalAlloc = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<long>(type: "INTEGER", nullable: false),
                    DirCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlexItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RatingKey = table.Column<string>(type: "TEXT", nullable: false),
                    SectionKey = table.Column<string>(type: "TEXT", nullable: false),
                    SectionTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    ShowTitle = table.Column<string>(type: "TEXT", nullable: true),
                    ParentTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastViewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PlexPath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", nullable: true),
                    Versions = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: true),
                    MatchedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlexItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Missing = table.Column<bool>(type: "INTEGER", nullable: false),
                    RootEntryId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastScanAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Condition = table.Column<string>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    StopProcessing = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RootId = table.Column<int>(type: "INTEGER", nullable: true),
                    SubPath = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FilesSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    DirsSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    Added = table.Column<long>(type: "INTEGER", nullable: false),
                    Changed = table.Column<long>(type: "INTEGER", nullable: false),
                    Removed = table.Column<long>(type: "INTEGER", nullable: false),
                    Hashed = table.Column<long>(type: "INTEGER", nullable: false),
                    Errors = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ActionItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: true),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false),
                    Op = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", nullable: true),
                    KeepPath = table.Column<string>(type: "TEXT", nullable: true),
                    KeepRootId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsDir = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpectedSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpectedMTime = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpectedHash = table.Column<string>(type: "TEXT", nullable: true),
                    Included = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Warning = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    UndoPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionItems_ActionPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "ActionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_PlanId_Status",
                table: "ActionItems",
                columns: new[] { "PlanId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AiResults_CacheKey",
                table: "AiResults",
                column: "CacheKey");

            migrationBuilder.CreateIndex(
                name: "IX_AiResults_EntryId",
                table: "AiResults",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Annotations_EntryId",
                table: "Annotations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Annotations_Source_Kind",
                table: "Annotations",
                columns: new[] { "Source", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Name",
                table: "Categories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirSizeHistory_RootId_RelPath",
                table: "DirSizeHistory",
                columns: new[] { "RootId", "RelPath" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_CategoryId",
                table: "Entries",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Ext",
                table: "Entries",
                column: "Ext");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_FullHash",
                table: "Entries",
                column: "FullHash");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_IsDir_Size",
                table: "Entries",
                columns: new[] { "IsDir", "Size" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_IsDir_TotalSize",
                table: "Entries",
                columns: new[] { "IsDir", "TotalSize" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_MTime",
                table: "Entries",
                column: "MTime");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_ParentId",
                table: "Entries",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RootId_RelPath",
                table: "Entries",
                columns: new[] { "RootId", "RelPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlexItems_EntryId",
                table: "PlexItems",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PlexItems_RatingKey",
                table: "PlexItems",
                column: "RatingKey");

            migrationBuilder.CreateIndex(
                name: "IX_PlexItems_ShowTitle",
                table: "PlexItems",
                column: "ShowTitle");

            migrationBuilder.CreateIndex(
                name: "IX_Roots_Path",
                table: "Roots",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Scans_StartedAt",
                table: "Scans",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionItems");

            migrationBuilder.DropTable(
                name: "ActionTemplates");

            migrationBuilder.DropTable(
                name: "AiResults");

            migrationBuilder.DropTable(
                name: "AiRuleSuggestions");

            migrationBuilder.DropTable(
                name: "Annotations");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "DirSizeHistory");

            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "PlexItems");

            migrationBuilder.DropTable(
                name: "Roots");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "Scans");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "ActionPlans");
        }
    }
}
