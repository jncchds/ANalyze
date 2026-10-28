namespace ANalyze.Domain;

/// <summary>Broad content kind, detected from the extension and magic bytes.</summary>
public enum FileKind
{
    Unknown = 0,
    Image,
    Video,
    Audio,
    Document,
    Pdf,
    Text,
    Code,
    Archive,
    DiskImage,
    Executable,
    Font,
    Database,
    Ebook,
    Model3D,
    Other,
}

/// <summary>Who assigned the category of an entry; a manual choice is never overwritten by rules or AI.</summary>
public enum CategorySource
{
    None = 0,
    Rule,
    Ai,
    Manual,
}

public enum ScanStatus
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Failed,
}

public enum ScanPhase
{
    Idle,
    Walking,
    Detecting,
    Categorizing,
    Hashing,
    Finishing,
}

public enum RuleSource
{
    User,
    Builtin,
    Ai,
}

public enum SuggestionStatus
{
    Pending,
    Accepted,
    Rejected,
}

public enum ActionOp
{
    /// <summary>Move into the root's trash folder (undoable).</summary>
    Trash,
    /// <summary>Permanently delete.</summary>
    Delete,
    /// <summary>Move/rename to a target path (undoable).</summary>
    Move,
    /// <summary>Replace a duplicate with a hard link to the kept copy.</summary>
    Hardlink,
}

public enum ConflictPolicy
{
    Skip,
    Rename,
    Overwrite,
}

public enum PlanSource
{
    Manual,
    Dedupe,
    Template,
    Insight,
}

public enum PlanStatus
{
    Draft,
    Running,
    Done,
    Failed,
    Undone,
    Cancelled,
}

public enum ItemStatus
{
    Pending,
    Done,
    Skipped,
    Failed,
    Undone,
}

public enum DedupeKeep
{
    Oldest,
    Newest,
    ShortestPath,
    LongestPath,
    PreferredFolder,
}

public enum AiPrivacy
{
    /// <summary>Only path, name, size, type and dates are sent.</summary>
    MetadataOnly,
    /// <summary>Also the first few KB of text files.</summary>
    TextSnippet,
}
