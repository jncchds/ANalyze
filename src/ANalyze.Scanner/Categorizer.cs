using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Scanner;

/// <summary>
/// Applies the rule set to files. Precedence: manual &gt; rule &gt; AI. Tags are always recomputed from rules.
/// </summary>
public sealed class Categorizer(SqliteSetup sqlite, IDbContextFactory<AnalyzeDbContext> dbf)
{
    private const int Page = 5000;

    public async Task<RuleEngine> LoadEngineAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return new RuleEngine(await db.Rules.AsNoTracking().ToListAsync(ct));
    }

    /// <summary>Re-evaluates rules for every file in scope. Returns how many files changed.</summary>
    public async Task<long> RunAsync(Scope scope, Action<long, long>? progress, CancellationToken ct)
    {
        var engine = await LoadEngineAsync(ct);
        using var db = sqlite.Open();
        return Run(db, engine, scope, progress, ct);
    }

    internal static long Run(SqliteConnection db, RuleEngine engine, Scope scope, Action<long, long>? progress, CancellationToken ct)
    {
        long total;
        using (var c = db.CreateCommand())
        {
            c.CommandText = $"SELECT COUNT(*) FROM Entries WHERE IsDir=0 {scope.Sql}";
            scope.Bind(c);
            total = (long)c.ExecuteScalar()!;
        }

        using var update = db.CreateCommand();
        update.CommandText = "UPDATE Entries SET CategoryId=$cat, CategorySource=$src, Tags=$tags WHERE Id=$id";
        foreach (var p in new[] { "$cat", "$src", "$tags", "$id" })
        {
            update.Parameters.Add(new SqliteParameter(p, null));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long lastId = 0, done = 0, changed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<(long Id, EntryFacts Facts, int? Cat, CategorySource Src, string? Tags)>(Page);
            using (var q = db.CreateCommand())
            {
                q.CommandText = $"""
                    SELECT Id, Name, RelPath, Ext, Mime, Kind, Size, MTime, ATime, Depth, CategoryId, CategorySource, Tags
                    FROM Entries WHERE IsDir=0 AND Id > $last {scope.Sql} ORDER BY Id LIMIT {Page}
                    """;
                q.Parameters.AddWithValue("$last", lastId);
                scope.Bind(q);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((r.GetInt64(0),
                        new EntryFacts(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                            (FileKind)r.GetInt32(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt32(9)),
                        r.IsDBNull(10) ? null : r.GetInt32(10), (CategorySource)r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12)));
                }
            }

            if (rows.Count == 0)
            {
                break;
            }

            lastId = rows[^1].Id;
            using var tx = db.BeginTransaction();
            update.Transaction = tx;
            foreach (var (id, facts, cat, src, tags) in rows)
            {
                var result = engine.Evaluate(facts, now);
                var newTags = result.Tags.Count == 0 ? null : "|" + string.Join('|', result.Tags) + "|";
                int? newCat;
                CategorySource newSrc;
                if (src == CategorySource.Manual)
                {
                    (newCat, newSrc) = (cat, src);
                }
                else if (result.CategoryId is { } rc)
                {
                    (newCat, newSrc) = (rc, CategorySource.Rule);
                }
                else if (src == CategorySource.Ai)
                {
                    (newCat, newSrc) = (cat, src);
                }
                else
                {
                    (newCat, newSrc) = (null, CategorySource.None);
                }

                if (newCat != cat || newSrc != src || newTags != tags)
                {
                    update.Parameters["$cat"].Value = (object?)newCat ?? DBNull.Value;
                    update.Parameters["$src"].Value = (int)newSrc;
                    update.Parameters["$tags"].Value = (object?)newTags ?? DBNull.Value;
                    update.Parameters["$id"].Value = id;
                    update.ExecuteNonQuery();
                    changed++;
                }
            }

            tx.Commit();
            done += rows.Count;
            progress?.Invoke(done, total);
        }

        return changed;
    }
}
