namespace ANalyze.Domain;

/// <summary>A mounted folder that is analyzed (e.g. /library). Configured through ANalyze__Roots.</summary>
public sealed class Root
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public bool ReadOnly { get; set; }
    public bool Missing { get; set; }
    public long? RootEntryId { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
}

/// <summary>
/// One file or directory. Directories carry aggregates of their subtree (Total*, FileCount, DirCount);
/// for files Total* equals their own size. Times are unix seconds.
/// </summary>
public sealed class Entry
{
    public long Id { get; set; }
    public int RootId { get; set; }
    public long? ParentId { get; set; }
    public required string Name { get; set; }
    /// <summary>Path relative to the root, '/' separated; "" for the root directory itself.</summary>
    public required string RelPath { get; set; }
    public int Depth { get; set; }
    public bool IsDir { get; set; }
    public bool IsSymlink { get; set; }
    public string? LinkTarget { get; set; }
    public bool BrokenLink { get; set; }
    public long Size { get; set; }
    public long AllocSize { get; set; }
    public long MTime { get; set; }
    public long ATime { get; set; }
    public long CTime { get; set; }
    public long Inode { get; set; }
    public long Dev { get; set; }
    public int NLink { get; set; }
    public string? Ext { get; set; }
    public string? Mime { get; set; }
    public FileKind Kind { get; set; }
    public bool Detected { get; set; }
    public int? CategoryId { get; set; }
    public CategorySource CategorySource { get; set; }
    /// <summary>Tags as "|a|b|" so a tag can be matched with LIKE '%|a|%'.</summary>
    public string? Tags { get; set; }
    public long? QuickHash { get; set; }
    public string? FullHash { get; set; }
    /// <summary>Unix seconds when <see cref="FullHash"/> was computed or last verified by re-reading the file.</summary>
    public long? HashedAt { get; set; }
    /// <summary>Unix seconds when a re-read found other content although size and mtime were unchanged (bit rot, or a program that restores timestamps).</summary>
    public long? CorruptAt { get; set; }
    public bool Error { get; set; }
    /// <summary>Directories: another filesystem is mounted here and was listed but not entered (cross-filesystem scanning is off).</summary>
    public bool OtherFilesystem { get; set; }

    public long TotalSize { get; set; }
    public long TotalAlloc { get; set; }
    public long FileCount { get; set; }
    public long DirCount { get; set; }
}

public sealed class ScanRun
{
    public long Id { get; set; }
    public int? RootId { get; set; }
    public string? SubPath { get; set; }
    public ScanStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long FilesSeen { get; set; }
    public long DirsSeen { get; set; }
    public long Added { get; set; }
    public long Changed { get; set; }
    public long Removed { get; set; }
    public long Hashed { get; set; }
    public long Errors { get; set; }
    public long TotalSize { get; set; }
    public string? Message { get; set; }
}

/// <summary>Size of top-level directories at the end of each full scan, to show growth over time.</summary>
public sealed class DirSizeHistory
{
    public long ScanId { get; set; }
    public long EntryId { get; set; }
    public int RootId { get; set; }
    public required string RelPath { get; set; }
    public long TotalSize { get; set; }
    public long FileCount { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#64748b";
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool BuiltIn { get; set; }
}

public sealed class Rule
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public RuleCondition Condition { get; set; } = new();
    public int? CategoryId { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>When set, rule matches stop categorization (first match wins) only if this rule assigns a category.</summary>
    public bool StopProcessing { get; set; } = true;
    public RuleSource Source { get; set; }
    public string? Description { get; set; }
}

public sealed class AiResult
{
    public long Id { get; set; }
    public required string CacheKey { get; set; }
    public long? EntryId { get; set; }
    public required string Model { get; set; }
    public required string Category { get; set; }
    public List<string> Tags { get; set; } = [];
    public double Confidence { get; set; }
    public string? Reason { get; set; }
    public bool Applied { get; set; }
    public bool Dismissed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AiRuleSuggestion
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string CategoryName { get; set; }
    public RuleCondition Condition { get; set; } = new();
    public List<string> Tags { get; set; } = [];
    public string? Rationale { get; set; }
    public int MatchCount { get; set; }
    public long MatchBytes { get; set; }
    public SuggestionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A reusable action, e.g. "move to /Sorted/{category}/{mtime:yyyy}/{name}".</summary>
public sealed class ActionTemplate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public ActionOp Op { get; set; }
    public string? Destination { get; set; }
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;
    public string? Description { get; set; }
}

public sealed class ActionPlan
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public PlanSource Source { get; set; }
    public PlanStatus Status { get; set; }
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;
    public bool VerifyHashes { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? TrashPurgedAt { get; set; }
    public string? Message { get; set; }
    public List<ActionItem> Items { get; set; } = [];
}

public sealed class ActionItem
{
    public long Id { get; set; }
    public int PlanId { get; set; }
    public long? EntryId { get; set; }
    public int RootId { get; set; }
    public ActionOp Op { get; set; }
    public required string SourcePath { get; set; }
    public string? TargetPath { get; set; }
    /// <summary>For Hardlink: the kept copy the duplicate is linked to (relative path in the same root).</summary>
    public string? KeepPath { get; set; }
    public int? KeepRootId { get; set; }
    public bool IsDir { get; set; }
    public long ExpectedSize { get; set; }
    public long ExpectedMTime { get; set; }
    public string? ExpectedHash { get; set; }
    public bool Included { get; set; } = true;
    public ItemStatus Status { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
    /// <summary>Where the item went (trash or move target) so it can be undone.</summary>
    public string? UndoPath { get; set; }
}

/// <summary>
/// A label an integration attaches to a file or folder (e.g. Plex: "Inception (2010)", "Library: Movies").
/// The browser shows them without knowing which integration wrote them.
/// </summary>
public sealed class EntryAnnotation
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    /// <summary>Integration id, e.g. "plex".</summary>
    public required string Source { get; set; }
    /// <summary>What the entry is for that integration: movie, episode, show, season, album, artist, library…</summary>
    public required string Kind { get; set; }
    public required string Label { get; set; }
    public string? Detail { get; set; }
    /// <summary>Optional state shown as a badge (e.g. watched / unwatched).</summary>
    public string? State { get; set; }
    /// <summary>Link to the item in the integration's own UI.</summary>
    public string? Url { get; set; }
}

/// <summary>One media file known to Plex (a movie version, an episode or a track), matched to a scanned entry.</summary>
public sealed class PlexItem
{
    public long Id { get; set; }
    public required string RatingKey { get; set; }
    public required string SectionKey { get; set; }
    public required string SectionTitle { get; set; }
    /// <summary>movie, episode or track.</summary>
    public required string Type { get; set; }
    public required string Title { get; set; }
    /// <summary>Show (episodes) or artist (tracks).</summary>
    public string? ShowTitle { get; set; }
    /// <summary>Season (episodes) or album (tracks).</summary>
    public string? ParentTitle { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? Year { get; set; }
    public int ViewCount { get; set; }
    public long? LastViewedAt { get; set; }
    public long? AddedAt { get; set; }
    public required string PlexPath { get; set; }
    /// <summary>Where ANalyze looked for <see cref="PlexPath"/>: the path after applying the path mappings.</summary>
    public string? CheckedPath { get; set; }
    public long Size { get; set; }
    public string? Resolution { get; set; }
    /// <summary>How many media versions the Plex item has (e.g. 1080p + 4K).</summary>
    public int Versions { get; set; } = 1;
    public long? EntryId { get; set; }
    /// <summary>How the file was matched: mapping, detected, name.</summary>
    public string? MatchedBy { get; set; }
    /// <summary>Link to the item in Plex Web.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// One file or folder another app knows about (an *arr movie file, a torrent, a Jellyfin episode, an Immich asset…),
/// matched to a scanned entry. Shared by all integrations built on <c>ItemIntegration</c>; Plex keeps its own table.
/// </summary>
public sealed class IntegrationItem
{
    public long Id { get; set; }
    /// <summary>Integration id, e.g. "radarr".</summary>
    public required string Source { get; set; }
    /// <summary>What the item is: movie, episode, track, torrent, download, asset…</summary>
    public required string Kind { get; set; }
    public required string ExternalId { get; set; }
    public required string Title { get; set; }
    /// <summary>Items are ranked by group: a movie, a show, an artist, a torrent.</summary>
    public string? GroupKey { get; set; }
    public string? GroupTitle { get; set; }
    /// <summary>Season, album, category…</summary>
    public string? SubTitle { get; set; }
    public int? Year { get; set; }
    public required string ExternalPath { get; set; }
    /// <summary>Where ANalyze looked for <see cref="ExternalPath"/>: the path after applying the path mappings.</summary>
    public string? CheckedPath { get; set; }
    public long Size { get; set; }
    public bool IsDir { get; set; }
    /// <summary>App-specific state: monitored, seeding, completed, watched, favorite…</summary>
    public string? State { get; set; }
    /// <summary>Quality, resolution, category, ratio…</summary>
    public string? Detail { get; set; }
    /// <summary>Unix seconds.</summary>
    public long? AddedAt { get; set; }
    /// <summary>Unix seconds: last played, completed…</summary>
    public long? LastUsedAt { get; set; }
    public int PlayCount { get; set; }
    public string? Url { get; set; }
    public long? EntryId { get; set; }
    /// <summary>How the item was matched: mapping, detected, name.</summary>
    public string? MatchedBy { get; set; }
}

/// <summary>
/// How a local file or folder compares with the parent ANalyze (the backup server): written by the "parent" integration for every
/// entry inside the backup folders, replaced on each sync.
/// </summary>
public sealed class BackupFile
{
    public long Id { get; set; }
    /// <summary>Files: synced, corrupt, missing or unsynced. Folders: synced or unsynced (see <c>BackupStates</c>).</summary>
    public required string State { get; set; }
    public bool IsDir { get; set; }
    public long EntryId { get; set; }
    public int LocalRootId { get; set; }
    public required string LocalRelPath { get; set; }
    /// <summary>Local size (folders: total size).</summary>
    public long Size { get; set; }
    /// <summary>Size on the parent; null when the parent has nothing at that path.</summary>
    public long? ParentSize { get; set; }
    /// <summary>Files: both content hashes exist and are equal.</summary>
    public bool Verified { get; set; }
    /// <summary>Folders: direct files and sub-folders here, and how many of them are synchronized.</summary>
    public int Children { get; set; }
    public int SyncedChildren { get; set; }
}

public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The file list of an archive (scan phase "archives"). Valid while the archive keeps <see cref="Size"/> and <see cref="MTime"/>;
/// otherwise it is listed again.
/// </summary>
public sealed class ArchiveListing
{
    /// <summary>The archive's entry (also the key).</summary>
    public long EntryId { get; set; }
    public long Size { get; set; }
    public long MTime { get; set; }
    /// <summary>zip, rar, 7z (headers read), or tar, gz… (streamed: decompressed once, CRC32 computed per file).</summary>
    public string? Format { get; set; }
    public bool Streamed { get; set; }
    public long ListedAt { get; set; }
    /// <summary>Why the archive could not be listed (encrypted headers, damaged, too many files…); no members then.</summary>
    public string? Error { get; set; }
    public int Files { get; set; }
    public long FileBytes { get; set; }
}

/// <summary>A file inside an archive (folders and links are not stored).</summary>
public sealed class ArchiveMember
{
    public long Id { get; set; }
    public long ArchiveId { get; set; }
    /// <summary>Path inside the archive, '/'-separated, without a leading slash.</summary>
    public required string Path { get; set; }
    public required string Name { get; set; }
    public long Size { get; set; }
    /// <summary>CRC32 of the content (from the archive's headers, or computed while streaming); null when unknown.</summary>
    public long? Crc { get; set; }
}

/// <summary>
/// A place where an archive's files were found extracted: archive member <c>p</c> (with <see cref="Strip"/> removed from its start)
/// is expected at <c>BaseRelPath/p</c> on <see cref="RootId"/>. Recomputed by scans; verification results survive as long as the
/// matched files keep their <see cref="Fingerprint"/>.
/// </summary>
public sealed class ArchiveExtraction
{
    public long Id { get; set; }
    public long ArchiveId { get; set; }
    public ExtractionState State { get; set; }
    public int RootId { get; set; }
    /// <summary>The folder the archive's paths start from ("" = the root).</summary>
    public required string BaseRelPath { get; set; }
    /// <summary>Leading folders of the archive's paths that the extraction left out ("" or "folder/").</summary>
    public required string Strip { get; set; }
    /// <summary>Deepest folder that holds all matched files (for display and links).</summary>
    public long? FolderId { get; set; }
    public required string FolderRelPath { get; set; }
    public int Files { get; set; }
    public int Matched { get; set; }
    public int Missing { get; set; }
    /// <summary>Present but smaller than in the archive: typical of an interrupted extraction.</summary>
    public int Smaller { get; set; }
    /// <summary>Present but larger than in the archive (or a folder in place of a file).</summary>
    public int Differs { get; set; }
    public long Bytes { get; set; }
    public long MatchedBytes { get; set; }
    /// <summary>Hash of the matched files' ids, sizes and mtimes: verification results are kept while it stays the same.</summary>
    public required string Fingerprint { get; set; }
    public long FoundAt { get; set; }
    /// <summary>When the extracted files were last compared with the archive's CRC32s.</summary>
    public long? VerifiedAt { get; set; }
    public int VerifiedOk { get; set; }
    public int VerifiedBad { get; set; }
    /// <summary>Matched files that could not be checked (no CRC in the archive, unreadable, changed since the scan).</summary>
    public int Unverified { get; set; }
    /// <summary>JSON array of archive paths whose extracted content differs from the archive (at most 500).</summary>
    public string? BadPaths { get; set; }
}

