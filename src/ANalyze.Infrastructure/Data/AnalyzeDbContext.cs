using System.Text.Json;
using ANalyze.Domain;
using ANalyze.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ANalyze.Infrastructure.Data;

public sealed class AnalyzeDbContext(DbContextOptions<AnalyzeDbContext> options) : DbContext(options)
{
    /// <summary>Partial index of files without a content hash, by size.</summary>
    public const string UnhashedIndex = "IX_Entries_Unhashed";

    public DbSet<Root> Roots => Set<Root>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<ScanRun> Scans => Set<ScanRun>();
    public DbSet<DirSizeHistory> DirSizeHistory => Set<DirSizeHistory>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<AiResult> AiResults => Set<AiResult>();
    public DbSet<AiRuleSuggestion> AiRuleSuggestions => Set<AiRuleSuggestion>();
    public DbSet<ActionTemplate> ActionTemplates => Set<ActionTemplate>();
    public DbSet<ActionPlan> ActionPlans => Set<ActionPlan>();
    public DbSet<ActionItem> ActionItems => Set<ActionItem>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<PlexItem> PlexItems => Set<PlexItem>();
    public DbSet<EntryAnnotation> Annotations => Set<EntryAnnotation>();
    public DbSet<IntegrationItem> IntegrationItems => Set<IntegrationItem>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite cannot order or compare DateTimeOffset stored as text; store it as a sortable number.
        b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Root>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
        });

        m.Entity<Entry>(e =>
        {
            e.HasIndex(x => new { x.RootId, x.RelPath }).IsUnique();
            e.HasIndex(x => x.ParentId);
            // Covers the duplicate size search (size + hard-link identity) without reading the table.
            e.HasIndex(x => new { x.IsDir, x.Size, x.IsSymlink, x.Inode, x.Dev });
            // Files with no content hash yet, by size: the hashing and duplicate counts read only this (named in their SQL).
            e.HasIndex(x => x.Size).HasDatabaseName(AnalyzeDbContext.UnhashedIndex).HasFilter("IsDir = 0 AND IsSymlink = 0 AND FullHash IS NULL");
            e.HasIndex(x => new { x.IsDir, x.TotalSize });
            e.HasIndex(x => x.FullHash);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.Ext);
            e.HasIndex(x => x.MTime);
            e.Property(x => x.Name).UseCollation("NOCASE");
        });

        m.Entity<ScanRun>(e => e.HasIndex(x => x.StartedAt));

        m.Entity<DirSizeHistory>(e =>
        {
            e.HasKey(x => new { x.ScanId, x.EntryId });
            e.HasIndex(x => new { x.RootId, x.RelPath });
        });

        m.Entity<Category>(e => e.HasIndex(x => x.Name).IsUnique());

        m.Entity<Rule>(e =>
        {
            e.Property(x => x.Condition).HasConversion(JsonConverter<RuleCondition>(), JsonComparer<RuleCondition>());
            e.Property(x => x.Tags).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
        });

        m.Entity<AiResult>(e =>
        {
            e.HasIndex(x => x.CacheKey);
            e.HasIndex(x => x.EntryId);
            e.Property(x => x.Tags).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
        });

        m.Entity<AiRuleSuggestion>(e =>
        {
            e.Property(x => x.Condition).HasConversion(JsonConverter<RuleCondition>(), JsonComparer<RuleCondition>());
            e.Property(x => x.Tags).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
        });

        m.Entity<ActionPlan>(e =>
        {
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<ActionItem>(e => e.HasIndex(x => new { x.PlanId, x.Status }));

        m.Entity<Setting>(e => e.HasKey(x => x.Key));

        m.Entity<EntryAnnotation>(e =>
        {
            e.HasIndex(x => x.EntryId);
            e.HasIndex(x => new { x.Source, x.Kind });
        });

        m.Entity<PlexItem>(e =>
        {
            e.HasIndex(x => x.EntryId);
            e.HasIndex(x => x.RatingKey);
            e.HasIndex(x => x.ShowTitle);
        });

        m.Entity<IntegrationItem>(e =>
        {
            e.HasIndex(x => x.EntryId);
            e.HasIndex(x => new { x.Source, x.Kind });
            e.HasIndex(x => new { x.Source, x.GroupKey });
        });

        m.Entity<BackupFile>(e =>
        {
            e.HasIndex(x => new { x.LocalRootId, x.LocalRelPath });
            e.HasIndex(x => x.EntryId);
            e.HasIndex(x => new { x.IsDir, x.State });
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, SettingsStore.Json), v => JsonSerializer.Deserialize<T>(v, SettingsStore.Json) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() =>
        new(
            (a, b) => JsonSerializer.Serialize(a, SettingsStore.Json) == JsonSerializer.Serialize(b, SettingsStore.Json),
            v => JsonSerializer.Serialize(v, SettingsStore.Json).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, SettingsStore.Json), SettingsStore.Json)!);
}
