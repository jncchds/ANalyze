using ANalyze.Domain;

namespace ANalyze.Infrastructure.Settings;

/// <summary>Settings edited in the UI, stored as JSON in the Settings table.</summary>
public sealed record AppSettings
{
    /// <summary>Cron expression (5 fields, UTC) for automatic rescans; empty = manual only.</summary>
    public string? ScanCron { get; init; }

    public bool ScanOnStartup { get; init; }

    /// <summary>
    /// Globs matched against the path relative to the root (and the bare name when the glob has no '/'); a glob starting with '/'
    /// also matches the full path ("/mnt/main/backups").
    /// </summary>
    public List<string> IgnorePatterns { get; init; } = [];

    /// <summary>
    /// Descend into other filesystems mounted below a root. On by default: in Docker every disk, dataset or subvolume below the library
    /// is another filesystem, and skipping them leaves folders that look empty.
    /// </summary>
    public bool CrossFilesystems { get; init; } = true;

    /// <summary>
    /// With <see cref="CrossFilesystems"/> off: other filesystems that are entered anyway, as full paths ANalyze sees. A path
    /// also allows the filesystems mounted below it ("/mnt/main/media" covers its child datasets).
    /// </summary>
    public List<string> EnterFilesystems { get; init; } = [];

    /// <summary>Read the first bytes of files whose extension is unknown to detect their type.</summary>
    public bool SniffUnknownFiles { get; init; } = true;

    public bool FindDuplicates { get; init; } = true;

    /// <summary>Files smaller than this are ignored by duplicate search.</summary>
    public long MinDuplicateSize { get; init; } = 1024;

    public int HashThreads { get; init; } = 2;

    /// <summary>Hash every file, not only duplicate candidates: needed to verify backups and to notice corruption.</summary>
    public bool HashAllFiles { get; init; }

    /// <summary>Re-read hashed files after this many days and flag content that changed without a new size or mtime; 0 = never.</summary>
    public int VerifyAfterDays { get; init; }

    /// <summary>Most data re-read for verification per scan, in GB; 0 = no limit.</summary>
    public int VerifyMaxGb { get; init; } = 200;

    public int TrashRetentionDays { get; init; } = 30;

    /// <summary>Re-hash both copies before deleting or linking a duplicate.</summary>
    public bool VerifyHashBeforeDelete { get; init; } = true;

    /// <summary>Number of top-level folders per root whose size history is kept per scan.</summary>
    public int HistoryDepth { get; init; } = 2;

    /// <summary>Folders pinned to the sidebar, as entry ids.</summary>
    public List<long> PinnedFolders { get; init; } = [];

    /// <summary>
    /// Where folders ANalyze sees are on the host (Docker bind mounts), for display only: "/library" → "/mnt/media".
    /// Mounts read from Docker fill the gaps.
    /// </summary>
    public List<HostPath> HostPaths { get; init; } = [];

    public AiSettings Ai { get; init; } = new();
}

/// <summary>A folder as ANalyze sees it (<see cref="Path"/>) and the same folder on the host machine.</summary>
public sealed record HostPath(string Path, string Host);

/// <summary>Host paths found automatically (the Docker integration reads ANalyze's own container mounts).</summary>
public interface IHostPathSource
{
    Task<IReadOnlyList<HostPath>> GetAsync(CancellationToken ct);
}

public sealed record AiSettings
{
    public bool Enabled { get; init; }

    /// <summary>OpenAI-compatible base URL, e.g. https://api.openai.com/v1 or http://ollama:11434/v1.</summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>Stored encrypted (data protection); never returned by the API.</summary>
    public string? ApiKey { get; init; }

    public string Model { get; init; } = "gpt-4o-mini";

    public int BatchSize { get; init; } = 25;

    public int MaxFilesPerRun { get; init; } = 500;

    public int MaxConcurrency { get; init; } = 2;

    public AiPrivacy Privacy { get; init; } = AiPrivacy.MetadataOnly;

    /// <summary>Results below this confidence are kept as suggestions instead of being applied.</summary>
    public double MinConfidence { get; init; } = 0.6;

    public bool AllowNewCategories { get; init; } = true;

    /// <summary>Use json_schema structured output; turn off for servers that only support json_object.</summary>
    public bool UseJsonSchema { get; init; } = true;

    public bool AutoClassifyAfterScan { get; init; }

    /// <summary>Extra hints for the model, e.g. "Files under Work/ are business documents".</summary>
    public string? ExtraInstructions { get; init; }
}
