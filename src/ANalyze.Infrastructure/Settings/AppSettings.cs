using ANalyze.Domain;

namespace ANalyze.Infrastructure.Settings;

/// <summary>Settings edited in the UI, stored as JSON in the Settings table.</summary>
public sealed record AppSettings
{
    /// <summary>Cron expression (5 fields, UTC) for automatic rescans; empty = manual only.</summary>
    public string? ScanCron { get; init; }

    public bool ScanOnStartup { get; init; }

    /// <summary>Globs matched against the path relative to the root (and the bare name when the glob has no '/').</summary>
    public List<string> IgnorePatterns { get; init; } = [];

    /// <summary>Descend into other filesystems mounted below a root.</summary>
    public bool CrossFilesystems { get; init; }

    /// <summary>Read the first bytes of files whose extension is unknown to detect their type.</summary>
    public bool SniffUnknownFiles { get; init; } = true;

    public bool FindDuplicates { get; init; } = true;

    /// <summary>Files smaller than this are ignored by duplicate search.</summary>
    public long MinDuplicateSize { get; init; } = 1024;

    public int HashThreads { get; init; } = 2;

    public int TrashRetentionDays { get; init; } = 30;

    /// <summary>Re-hash both copies before deleting or linking a duplicate.</summary>
    public bool VerifyHashBeforeDelete { get; init; } = true;

    /// <summary>Number of top-level folders per root whose size history is kept per scan.</summary>
    public int HistoryDepth { get; init; } = 2;

    /// <summary>Folders pinned to the sidebar, as entry ids.</summary>
    public List<long> PinnedFolders { get; init; } = [];

    public AiSettings Ai { get; init; } = new();
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
