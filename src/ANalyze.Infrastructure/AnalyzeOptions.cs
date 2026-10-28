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

    public string DatabasePath => Path.Combine(DataDir, "analyze.db");

    public string ThumbsDir => Path.Combine(DataDir, "thumbs");
}
