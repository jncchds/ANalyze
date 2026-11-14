namespace ANalyze.Infrastructure;

/// <summary>Startup configuration (section "ANalyze", env vars ANalyze__X).</summary>
public sealed class AnalyzeOptions
{
    public const string Section = "ANalyze";

    /// <summary>Holds analyze.db, thumbnails, logs and data-protection keys.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Folders to analyze. Each mount is one root (ANalyze__Roots__0=/library, ANalyze__Roots__1=/media…).</summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>Treat every root as read-only even when it is writable: analysis only, no actions.</summary>
    public bool ReadOnly { get; set; }

    public bool AutoMigrate { get; set; } = true;

    /// <summary>When set, the UI and API require this password.</summary>
    public string? Password { get; set; }

    /// <summary>Optional token for scripts: send "Authorization: Bearer &lt;token&gt;".</summary>
    public string? ApiToken { get; set; }

    /// <summary>Where the index is stored: SQLite (default, <c>analyze.db</c> in the data folder) or PostgreSQL.</summary>
    public DatabaseOptions Database { get; set; } = new();

    public string DatabasePath => Path.Combine(DataDir, "analyze.db");

    public string ThumbsDir => Path.Combine(DataDir, "thumbs");
}

/// <summary>Section "ANalyze:Database" (env vars ANalyze__Database__X).</summary>
public sealed class DatabaseOptions
{
    /// <summary>"sqlite" (default) or "postgres".</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>
    /// PostgreSQL: required, e.g. "Host=db;Database=analyze;Username=analyze;Password=…". SQLite: optional, overrides the file
    /// in the data folder.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// PostgreSQL only: an ANalyze SQLite database (e.g. /data/analyze.db) to copy into PostgreSQL at startup while the
    /// PostgreSQL database has never been scanned. Ignored afterwards, so it can stay set.
    /// </summary>
    public string? ImportFrom { get; set; }
}
