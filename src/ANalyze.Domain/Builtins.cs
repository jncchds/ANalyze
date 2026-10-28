namespace ANalyze.Domain;

/// <summary>
/// Categories, rules and action templates shipped with ANalyze. They are seeded once into an empty database
/// and are fully editable afterwards (the "Restore built-in rules" action re-adds missing ones).
/// </summary>
public static class Builtins
{
    public sealed record CategorySeed(string Name, string Color, string Icon, string Description);

    public sealed record RuleSeed(string Name, string? Category, RuleCondition Condition, string[] Tags, string Description, bool Stop = true);

    // Colors: a categorical palette that stays distinguishable in light and dark themes.
    public static readonly CategorySeed[] Categories =
    [
        new("Video", "#2563eb", "film", "Movies, series, clips and their subtitles"),
        new("Audio", "#7c3aed", "music", "Music, podcasts, audiobooks, playlists"),
        new("Images", "#0891b2", "image", "Photos, pictures, screenshots"),
        new("RAW photos", "#0e7490", "camera", "Camera RAW files (CR2, NEF, ARW, DNG…)"),
        new("Documents", "#16a34a", "file-text", "Office documents, PDFs, plain text"),
        new("Ebooks", "#65a30d", "book-open", "EPUB, FB2, MOBI, comics"),
        new("Archives", "#ca8a04", "archive", "ZIP, RAR, 7z, tarballs"),
        new("Disk images", "#ea580c", "disc", "ISO images, VM disks"),
        new("Installers & executables", "#dc2626", "package", "Setup files, packages, binaries"),
        new("Code", "#4f46e5", "code", "Source code and version control data"),
        new("Dev junk", "#db2777", "trash-2", "Reproducible build output and dependency folders (node_modules, obj, __pycache__…)"),
        new("Caches & temp", "#9333ea", "timer-reset", "Caches, thumbnails, temporary and swap files"),
        new("Incomplete downloads", "#e11d48", "download", "Partial downloads and torrent leftovers"),
        new("Backups", "#0d9488", "history", "Backup copies and old versions"),
        new("Logs", "#78716c", "scroll-text", "Log files and rotated logs"),
        new("Fonts", "#a16207", "type", "Font files"),
        new("Databases", "#475569", "database", "Database files"),
        new("3D & design", "#be185d", "shapes", "3D models, CAD, layered design sources"),
        new("System files", "#64748b", "settings", "OS and NAS metadata (Thumbs.db, .DS_Store, @eaDir…)"),
    ];

    private static RuleClause C(RuleField field, RuleOp op, string value, bool negate = false) =>
        new() { Field = field, Op = op, Value = value, Negate = negate };

    private static RuleCondition All(params RuleClause[] clauses) => new() { Match = MatchMode.All, Clauses = [.. clauses] };

    private static RuleCondition Any(params RuleClause[] clauses) => new() { Match = MatchMode.Any, Clauses = [.. clauses] };

    public static readonly RuleSeed[] Rules =
    [
        // Tag-only rules first: they never stop processing.
        new("Tag huge files", null, All(C(RuleField.Size, RuleOp.GreaterThan, "4GB")), ["huge"], "Files larger than 4 GB", Stop: false),
        new("Tag stale files", null, All(C(RuleField.ModifiedDays, RuleOp.GreaterThan, "1825")), ["stale"], "Not modified for 5+ years", Stop: false),
        new("Tag empty files", null, All(C(RuleField.Size, RuleOp.Equals, "0")), ["empty"], "Zero-byte files", Stop: false),
        new("Tag copies", null, Any(
            C(RuleField.Name, RuleOp.Regex, @"\(\d+\)\.[^.]+$"),
            C(RuleField.Name, RuleOp.Regex, @"[ _-](copy|копия|kopie|copia)( \d+)?\.[^.]+$"),
            C(RuleField.Name, RuleOp.Regex, @"^copy of ")), ["copy-name"], "Names like 'file (1).jpg' or 'file - Copy.docx'", Stop: false),

        // Specific junk and metadata before generic file kinds.
        new("NAS & OS metadata folders", "System files", All(C(RuleField.PathSegment, RuleOp.In,
            "@eaDir,#recycle,@Recycle,@Recently-Snapshot,.@__thumb,#snapshot,.Spotlight-V100,.fseventsd,.TemporaryItems,System Volume Information,$RECYCLE.BIN,lost+found")),
            ["junk"], "Synology/QNAP thumbnails and recycle bins, macOS and Windows system folders"),
        new("OS metadata files", "System files", Any(
            C(RuleField.Name, RuleOp.In, "Thumbs.db,ehthumbs.db,ehthumbs_vista.db,desktop.ini,.DS_Store,.localized,.directory"),
            C(RuleField.Name, RuleOp.StartsWith, "._")), ["junk"], "Thumbs.db, .DS_Store, AppleDouble '._' files"),
        new("Dependency folders", "Dev junk", All(C(RuleField.PathSegment, RuleOp.In,
            "node_modules,bower_components,jspm_packages,.venv,venv,.virtualenv,__pycache__,.tox,.nox,.mypy_cache,.pytest_cache,.ruff_cache,.gradle,.m2,.nuget,.pnpm-store,.yarn,.cargo,.rustup,site-packages")),
            ["dependencies", "reproducible"], "Package manager folders that can be restored with an install"),
        new("Build output", "Dev junk", Any(
            C(RuleField.PathSegment, RuleOp.In, "obj,.next,.nuxt,.svelte-kit,.parcel-cache,.turbo,.angular,.expo,.terraform,cmake-build-debug,cmake-build-release,DerivedData"),
            C(RuleField.Extension, RuleOp.In, "pyc,pyo,class,o,obj.d,pdb,ilk,tsbuildinfo")), ["build", "reproducible"], "Compiler and bundler output"),
        new("Caches", "Caches & temp", Any(
            C(RuleField.PathSegment, RuleOp.In, ".cache,cache,Cache,Caches,.thumbnails,thumbnails,.Trash,.Trash-1000,GPUCache,Code Cache,ShaderCache,CachedData"),
            C(RuleField.Extension, RuleOp.In, "cache")), ["cache"], "Application caches and thumbnail stores"),
        new("Temporary files", "Caches & temp", Any(
            C(RuleField.Extension, RuleOp.In, "tmp,temp,swp,swo,swn,dmp,crash,~tmp"),
            C(RuleField.Name, RuleOp.EndsWith, "~"),
            C(RuleField.Name, RuleOp.StartsWith, "~$"),
            C(RuleField.Name, RuleOp.Glob, "core.[0-9]*")), ["temp"], "Temp, swap, crash dumps, Office lock files"),
        new("Incomplete downloads", "Incomplete downloads", Any(
            C(RuleField.Extension, RuleOp.In, "part,crdownload,partial,download,opdownload,!qb,!ut,aria2,bc!,xltd,dlpart"),
            C(RuleField.PathSegment, RuleOp.In, "incomplete,.incomplete")), ["incomplete"], "Unfinished browser and torrent downloads"),
        new("Torrent files", "Incomplete downloads", All(C(RuleField.Extension, RuleOp.In, "torrent,magnet")), ["torrent"], ".torrent descriptors"),
        new("Version control data", "Code", All(C(RuleField.PathSegment, RuleOp.In, ".git,.svn,.hg,.bzr")), ["vcs"], "Git/SVN/Mercurial internals"),
        new("Logs", "Logs", Any(
            C(RuleField.Name, RuleOp.Regex, @"\.log(\.\d+)?(\.(gz|bz2|xz|zip))?$"),
            C(RuleField.Extension, RuleOp.In, "log,etl,evtx,journal")), ["log"], "Log files including rotated ones"),
        new("Backups", "Backups", Any(
            C(RuleField.Extension, RuleOp.In, "bak,bkp,old,orig,backup,tib,tibx,vbk,vib,bkf,abk,nbk,gho,wbcat,sbf,ab,tbk"),
            C(RuleField.PathSegment, RuleOp.In, "Backups,backup,backups,Backup,.snapshots,Time Machine Backups"),
            C(RuleField.Name, RuleOp.Regex, @"(^|[ ._-])backup([ ._-]|\d|$)")), ["backup"], "Backup files and backup folders"),

        // Media
        new("Screenshots", "Images", All(
            C(RuleField.Kind, RuleOp.Equals, "Image"),
            C(RuleField.Name, RuleOp.Regex, @"^(screenshot|screen shot|scr(een)?_?\d|снимок экрана|скриншот|bildschirmfoto|capture d.écran)")), ["screenshot"], "Screenshots by common name patterns"),
        new("Camera RAW", "RAW photos", All(C(RuleField.Extension, RuleOp.In, "cr2,cr3,crw,nef,nrw,arw,srf,sr2,dng,orf,rw2,raf,srw,pef,x3f,erf,3fr,iiq,mos,mrw,rwl")), ["raw"], "Camera RAW formats"),
        new("Images", "Images", All(C(RuleField.Kind, RuleOp.Equals, "Image")), [], "Any image"),
        new("Subtitles", "Video", All(C(RuleField.Extension, RuleOp.In, "srt,ass,ssa,sub,idx,vtt,sup,smi")), ["subtitles"], "Subtitle tracks next to videos"),
        new("Video", "Video", All(C(RuleField.Kind, RuleOp.Equals, "Video")), [], "Any video"),
        new("Playlists & cue sheets", "Audio", All(C(RuleField.Extension, RuleOp.In, "m3u,m3u8,pls,cue,xspf")), ["playlist"], "Playlists and CUE sheets"),
        new("Audio", "Audio", All(C(RuleField.Kind, RuleOp.Equals, "Audio")), [], "Any audio"),
        new("Ebooks & comics", "Ebooks", All(C(RuleField.Kind, RuleOp.Equals, "Ebook")), [], "EPUB, FB2, MOBI, CBZ…"),
        new("Documents", "Documents", All(C(RuleField.Kind, RuleOp.In, "Document,Pdf")), [], "PDF and office documents"),
        new("Disk images", "Disk images", All(C(RuleField.Kind, RuleOp.Equals, "DiskImage")), [], "ISO and virtual machine disks"),
        new("Archives", "Archives", All(C(RuleField.Kind, RuleOp.Equals, "Archive")), [], "Compressed archives"),
        new("Installers & executables", "Installers & executables", All(C(RuleField.Kind, RuleOp.Equals, "Executable")), [], "Programs and packages"),
        new("Fonts", "Fonts", All(C(RuleField.Kind, RuleOp.Equals, "Font")), [], "Font files"),
        new("Databases", "Databases", All(C(RuleField.Kind, RuleOp.Equals, "Database")), [], "Database files"),
        new("3D & design sources", "3D & design", Any(
            C(RuleField.Kind, RuleOp.Equals, "Model3D"),
            C(RuleField.Extension, RuleOp.In, "psd,psb,ai,indd,sketch,fig,xd,xcf,kra,afdesign,afphoto,cdr,blend")), [], "3D models, CAD and layered design files"),
        new("Source code", "Code", All(C(RuleField.Kind, RuleOp.Equals, "Code")), [], "Source code"),
        new("Plain text", "Documents", All(C(RuleField.Kind, RuleOp.Equals, "Text")), ["text"], "Text files"),
    ];

    public sealed record TemplateSeed(string Name, ActionOp Op, string? Destination, ConflictPolicy Conflict, string Description);

    public static readonly TemplateSeed[] Templates =
    [
        new("Move to trash", ActionOp.Trash, null, ConflictPolicy.Rename, "Move into the root's .analyze-trash folder; undoable until the trash is emptied"),
        new("Delete permanently", ActionOp.Delete, null, ConflictPolicy.Skip, "Remove for good — cannot be undone"),
        new("Sort by category and year", ActionOp.Move, "Sorted/{category}/{mtime:yyyy}/{name}", ConflictPolicy.Rename, "e.g. Sorted/Images/2021/IMG_0001.jpg"),
        new("Sort by category", ActionOp.Move, "Sorted/{category}/{name}", ConflictPolicy.Rename, "e.g. Sorted/Documents/report.pdf"),
        new("Photos by date", ActionOp.Move, "Photos/{mtime:yyyy}/{mtime:yyyy-MM}/{name}", ConflictPolicy.Rename, "e.g. Photos/2023/2023-07/IMG_0001.jpg"),
        new("Group by extension", ActionOp.Move, "By type/{ext}/{name}", ConflictPolicy.Rename, "e.g. By type/iso/ubuntu.iso"),
        new("Archive old files", ActionOp.Move, "Archive/{mtime:yyyy}/{relDir}/{name}", ConflictPolicy.Rename, "Keeps the folder structure under Archive/<year>"),
    ];
}
