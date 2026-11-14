using ANalyze.Domain;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Infrastructure.Data;

/// <summary>
/// The same model on PostgreSQL, with its own migrations (<c>Data/PostgresMigrations</c>). Differences from SQLite:
/// <list type="bullet">
/// <item>Table and column names are lower case, so the raw SQL's unquoted <c>Entries.RelPath</c> finds <c>entries.relpath</c>.</item>
/// <item>Text columns use the "C" collation (byte order, like SQLite's BINARY): the subtree ranges <c>RelPath &gt;= 'p/' AND &lt; 'p0'</c>
/// rely on '/' sorting right before '0'. <c>Entries.Name</c> uses <c>nocase</c>, an ICU collation that orders case-insensitively
/// (SQLite's NOCASE); raw SQL can name it the same way: <c>COLLATE NOCASE</c> folds to <c>nocase</c>.</item>
/// <item>Booleans are real booleans; the partial index filter is written in PostgreSQL syntax.</item>
/// </list>
/// </summary>
public sealed class PostgresDbContext(DbContextOptions<PostgresDbContext> options) : AnalyzeDbContext(options)
{
    public const string NoCase = "nocase";

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);
        m.HasCollation(NoCase, locale: "und-u-ks-level2", provider: "icu", deterministic: true);
        m.Entity<Entry>().HasIndex(x => x.Size).HasDatabaseName(UnhashedIndex)
            .HasFilter("isdir = FALSE AND issymlink = FALSE AND fullhash IS NULL");

        foreach (var t in m.Model.GetEntityTypes())
        {
            t.SetTableName(t.GetTableName()!.ToLowerInvariant());
            foreach (var p in t.GetProperties())
            {
                p.SetColumnName(p.GetColumnName().ToLowerInvariant());
                if ((p.GetValueConverter()?.ProviderClrType ?? p.ClrType) == typeof(string))
                {
                    p.SetCollation(string.Equals(p.GetCollation(), "NOCASE", StringComparison.OrdinalIgnoreCase) ? NoCase : "C");
                }
            }
        }
    }
}
