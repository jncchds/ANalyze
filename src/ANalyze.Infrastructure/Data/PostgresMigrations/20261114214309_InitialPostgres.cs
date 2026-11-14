using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ANalyze.Infrastructure.Data.PostgresMigrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:nocase", "und-u-ks-level2,und-u-ks-level2,icu,True");

            migrationBuilder.CreateTable(
                name: "actionplans",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    conflict = table.Column<int>(type: "integer", nullable: false),
                    verifyhashes = table.Column<bool>(type: "boolean", nullable: false),
                    createdat = table.Column<long>(type: "bigint", nullable: false),
                    appliedat = table.Column<long>(type: "bigint", nullable: true),
                    trashpurgedat = table.Column<long>(type: "bigint", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actionplans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "actiontemplates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    op = table.Column<int>(type: "integer", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    conflict = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actiontemplates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "airesults",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    cachekey = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    entryid = table.Column<long>(type: "bigint", nullable: true),
                    model = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    category = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    tags = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    applied = table.Column<bool>(type: "boolean", nullable: false),
                    dismissed = table.Column<bool>(type: "boolean", nullable: false),
                    createdat = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airesults", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "airulesuggestions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    categoryname = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    condition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    tags = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    rationale = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    matchcount = table.Column<int>(type: "integer", nullable: false),
                    matchbytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    createdat = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airulesuggestions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "annotations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entryid = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    kind = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    label = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    detail = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    state = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    url = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_annotations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archiveextractions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    archiveid = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    rootid = table.Column<int>(type: "integer", nullable: false),
                    baserelpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    strip = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    folderid = table.Column<long>(type: "bigint", nullable: true),
                    folderrelpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    files = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    missing = table.Column<int>(type: "integer", nullable: false),
                    smaller = table.Column<int>(type: "integer", nullable: false),
                    differs = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    matchedbytes = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    foundat = table.Column<long>(type: "bigint", nullable: false),
                    verifiedat = table.Column<long>(type: "bigint", nullable: true),
                    verifiedok = table.Column<int>(type: "integer", nullable: false),
                    verifiedbad = table.Column<int>(type: "integer", nullable: false),
                    unverified = table.Column<int>(type: "integer", nullable: false),
                    badpaths = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archiveextractions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archivelistings",
                columns: table => new
                {
                    entryid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    mtime = table.Column<long>(type: "bigint", nullable: false),
                    format = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    streamed = table.Column<bool>(type: "boolean", nullable: false),
                    listedat = table.Column<long>(type: "bigint", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    files = table.Column<int>(type: "integer", nullable: false),
                    filebytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archivelistings", x => x.entryid);
                });

            migrationBuilder.CreateTable(
                name: "archivemembers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    archiveid = table.Column<long>(type: "bigint", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    crc = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archivemembers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backupfiles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    state = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    isdir = table.Column<bool>(type: "boolean", nullable: false),
                    entryid = table.Column<long>(type: "bigint", nullable: false),
                    localrootid = table.Column<int>(type: "integer", nullable: false),
                    localrelpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    parentsize = table.Column<long>(type: "bigint", nullable: true),
                    parenthash = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    verified = table.Column<bool>(type: "boolean", nullable: false),
                    children = table.Column<int>(type: "integer", nullable: false),
                    syncedchildren = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backupfiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    color = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    icon = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    description = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    sortorder = table.Column<int>(type: "integer", nullable: false),
                    builtin = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dirsizehistory",
                columns: table => new
                {
                    scanid = table.Column<long>(type: "bigint", nullable: false),
                    entryid = table.Column<long>(type: "bigint", nullable: false),
                    rootid = table.Column<int>(type: "integer", nullable: false),
                    relpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    totalsize = table.Column<long>(type: "bigint", nullable: false),
                    filecount = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dirsizehistory", x => new { x.scanid, x.entryid });
                });

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rootid = table.Column<int>(type: "integer", nullable: false),
                    parentid = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false, collation: "nocase"),
                    relpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    isdir = table.Column<bool>(type: "boolean", nullable: false),
                    issymlink = table.Column<bool>(type: "boolean", nullable: false),
                    linktarget = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    brokenlink = table.Column<bool>(type: "boolean", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    allocsize = table.Column<long>(type: "bigint", nullable: false),
                    mtime = table.Column<long>(type: "bigint", nullable: false),
                    atime = table.Column<long>(type: "bigint", nullable: false),
                    ctime = table.Column<long>(type: "bigint", nullable: false),
                    inode = table.Column<long>(type: "bigint", nullable: false),
                    dev = table.Column<long>(type: "bigint", nullable: false),
                    nlink = table.Column<int>(type: "integer", nullable: false),
                    ext = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    mime = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    detected = table.Column<bool>(type: "boolean", nullable: false),
                    categoryid = table.Column<int>(type: "integer", nullable: true),
                    categorysource = table.Column<int>(type: "integer", nullable: false),
                    tags = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    quickhash = table.Column<long>(type: "bigint", nullable: true),
                    fullhash = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    hashedat = table.Column<long>(type: "bigint", nullable: true),
                    corruptat = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<bool>(type: "boolean", nullable: false),
                    otherfilesystem = table.Column<bool>(type: "boolean", nullable: false),
                    totalsize = table.Column<long>(type: "bigint", nullable: false),
                    totalalloc = table.Column<long>(type: "bigint", nullable: false),
                    filecount = table.Column<long>(type: "bigint", nullable: false),
                    dircount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integrationitems",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    kind = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    externalid = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    title = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    groupkey = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    grouptitle = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    subtitle = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    year = table.Column<int>(type: "integer", nullable: true),
                    externalpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    checkedpath = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    isdir = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    detail = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    addedat = table.Column<long>(type: "bigint", nullable: true),
                    lastusedat = table.Column<long>(type: "bigint", nullable: true),
                    playcount = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    entryid = table.Column<long>(type: "bigint", nullable: true),
                    matchedby = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrationitems", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plexitems",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ratingkey = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    sectionkey = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    sectiontitle = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    type = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    title = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    showtitle = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    parenttitle = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    season = table.Column<int>(type: "integer", nullable: true),
                    episode = table.Column<int>(type: "integer", nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    viewcount = table.Column<int>(type: "integer", nullable: false),
                    lastviewedat = table.Column<long>(type: "bigint", nullable: true),
                    addedat = table.Column<long>(type: "bigint", nullable: true),
                    plexpath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    checkedpath = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    resolution = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    versions = table.Column<int>(type: "integer", nullable: false),
                    entryid = table.Column<long>(type: "bigint", nullable: true),
                    matchedby = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    url = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plexitems", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roots",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    path = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    @readonly = table.Column<bool>(name: "readonly", type: "boolean", nullable: false),
                    missing = table.Column<bool>(type: "boolean", nullable: false),
                    rootentryid = table.Column<long>(type: "bigint", nullable: true),
                    lastscanat = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    order = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    condition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    categoryid = table.Column<int>(type: "integer", nullable: true),
                    tags = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    stopprocessing = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rootid = table.Column<int>(type: "integer", nullable: true),
                    subpath = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    status = table.Column<int>(type: "integer", nullable: false),
                    startedat = table.Column<long>(type: "bigint", nullable: false),
                    finishedat = table.Column<long>(type: "bigint", nullable: true),
                    filesseen = table.Column<long>(type: "bigint", nullable: false),
                    dirsseen = table.Column<long>(type: "bigint", nullable: false),
                    added = table.Column<long>(type: "bigint", nullable: false),
                    changed = table.Column<long>(type: "bigint", nullable: false),
                    removed = table.Column<long>(type: "bigint", nullable: false),
                    hashed = table.Column<long>(type: "bigint", nullable: false),
                    errors = table.Column<long>(type: "bigint", nullable: false),
                    totalsize = table.Column<long>(type: "bigint", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    value = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    updatedat = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "actionitems",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    planid = table.Column<int>(type: "integer", nullable: false),
                    entryid = table.Column<long>(type: "bigint", nullable: true),
                    rootid = table.Column<int>(type: "integer", nullable: false),
                    op = table.Column<int>(type: "integer", nullable: false),
                    sourcepath = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    targetpath = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    keeppath = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    keeprootid = table.Column<int>(type: "integer", nullable: true),
                    isdir = table.Column<bool>(type: "boolean", nullable: false),
                    expectedsize = table.Column<long>(type: "bigint", nullable: false),
                    expectedmtime = table.Column<long>(type: "bigint", nullable: false),
                    expectedhash = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    included = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    warning = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    error = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    undopath = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actionitems", x => x.id);
                    table.ForeignKey(
                        name: "FK_actionitems_actionplans_planid",
                        column: x => x.planid,
                        principalTable: "actionplans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_actionitems_planid_status",
                table: "actionitems",
                columns: new[] { "planid", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_airesults_cachekey",
                table: "airesults",
                column: "cachekey");

            migrationBuilder.CreateIndex(
                name: "IX_airesults_entryid",
                table: "airesults",
                column: "entryid");

            migrationBuilder.CreateIndex(
                name: "IX_annotations_entryid",
                table: "annotations",
                column: "entryid");

            migrationBuilder.CreateIndex(
                name: "IX_annotations_source_kind",
                table: "annotations",
                columns: new[] { "source", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_archiveextractions_archiveid",
                table: "archiveextractions",
                column: "archiveid");

            migrationBuilder.CreateIndex(
                name: "IX_archiveextractions_state_matchedbytes",
                table: "archiveextractions",
                columns: new[] { "state", "matchedbytes" });

            migrationBuilder.CreateIndex(
                name: "IX_archivemembers_archiveid",
                table: "archivemembers",
                column: "archiveid");

            migrationBuilder.CreateIndex(
                name: "IX_backupfiles_entryid",
                table: "backupfiles",
                column: "entryid");

            migrationBuilder.CreateIndex(
                name: "IX_backupfiles_isdir_state",
                table: "backupfiles",
                columns: new[] { "isdir", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_backupfiles_localrootid_localrelpath",
                table: "backupfiles",
                columns: new[] { "localrootid", "localrelpath" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_name",
                table: "categories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dirsizehistory_rootid_relpath",
                table: "dirsizehistory",
                columns: new[] { "rootid", "relpath" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_categoryid",
                table: "entries",
                column: "categoryid");

            migrationBuilder.CreateIndex(
                name: "IX_entries_ext",
                table: "entries",
                column: "ext");

            migrationBuilder.CreateIndex(
                name: "IX_entries_fullhash",
                table: "entries",
                column: "fullhash");

            migrationBuilder.CreateIndex(
                name: "IX_entries_isdir_size_issymlink_inode_dev",
                table: "entries",
                columns: new[] { "isdir", "size", "issymlink", "inode", "dev" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_isdir_totalsize",
                table: "entries",
                columns: new[] { "isdir", "totalsize" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_mtime",
                table: "entries",
                column: "mtime");

            migrationBuilder.CreateIndex(
                name: "IX_entries_parentid",
                table: "entries",
                column: "parentid");

            migrationBuilder.CreateIndex(
                name: "IX_entries_rootid_hashedat",
                table: "entries",
                columns: new[] { "rootid", "hashedat" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_rootid_relpath",
                table: "entries",
                columns: new[] { "rootid", "relpath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_Unhashed",
                table: "entries",
                column: "size",
                filter: "isdir = FALSE AND issymlink = FALSE AND fullhash IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_integrationitems_entryid",
                table: "integrationitems",
                column: "entryid");

            migrationBuilder.CreateIndex(
                name: "IX_integrationitems_source_groupkey",
                table: "integrationitems",
                columns: new[] { "source", "groupkey" });

            migrationBuilder.CreateIndex(
                name: "IX_integrationitems_source_kind",
                table: "integrationitems",
                columns: new[] { "source", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_plexitems_entryid",
                table: "plexitems",
                column: "entryid");

            migrationBuilder.CreateIndex(
                name: "IX_plexitems_ratingkey",
                table: "plexitems",
                column: "ratingkey");

            migrationBuilder.CreateIndex(
                name: "IX_plexitems_showtitle",
                table: "plexitems",
                column: "showtitle");

            migrationBuilder.CreateIndex(
                name: "IX_roots_path",
                table: "roots",
                column: "path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scans_startedat",
                table: "scans",
                column: "startedat");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actionitems");

            migrationBuilder.DropTable(
                name: "actiontemplates");

            migrationBuilder.DropTable(
                name: "airesults");

            migrationBuilder.DropTable(
                name: "airulesuggestions");

            migrationBuilder.DropTable(
                name: "annotations");

            migrationBuilder.DropTable(
                name: "archiveextractions");

            migrationBuilder.DropTable(
                name: "archivelistings");

            migrationBuilder.DropTable(
                name: "archivemembers");

            migrationBuilder.DropTable(
                name: "backupfiles");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "dirsizehistory");

            migrationBuilder.DropTable(
                name: "entries");

            migrationBuilder.DropTable(
                name: "integrationitems");

            migrationBuilder.DropTable(
                name: "plexitems");

            migrationBuilder.DropTable(
                name: "roots");

            migrationBuilder.DropTable(
                name: "rules");

            migrationBuilder.DropTable(
                name: "scans");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "actionplans");
        }
    }
}
