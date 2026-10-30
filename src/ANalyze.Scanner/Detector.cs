using ANalyze.Domain;
using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>Fills Kind/Mime for files not detected yet: by extension first, by magic bytes when the extension is unknown.</summary>
internal static class Detector
{
    private const int Page = 2000;
    private const int HeaderBytes = 4096;

    public static void Run(SqliteConnection db, ScanContext ctx, IReadOnlyDictionary<int, string> rootPaths, Scope scope, bool sniff)
    {
        ctx.StartPhase("detecting", Count(db, scope));
        ctx.Log.LogInformation(
            "Detecting file types: {Count} new or changed files get their type (video, image, document…) from the extension{Sniff}. Folder sizes are already final",
            ctx.Total, sniff ? ", or from their first 4 KB when the extension is unknown" : "");
        long byExtension = 0, sniffed = 0, unknown = 0;

        using var update = db.CreateCommand();
        update.CommandText = "UPDATE Entries SET Kind=$kind, Mime=$mime, Detected=1 WHERE Id=$id";
        update.Parameters.Add(new SqliteParameter("$kind", null));
        update.Parameters.Add(new SqliteParameter("$mime", null));
        update.Parameters.Add(new SqliteParameter("$id", null));

        long lastId = 0;
        while (true)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var page = new List<(long Id, int RootId, string RelPath, string? Ext, long Size)>(Page);
            using (var q = db.CreateCommand())
            {
                q.CommandText = $"SELECT Id, RootId, RelPath, Ext, Size FROM Entries WHERE IsDir=0 AND Detected=0 AND Id > $last {scope.Sql} ORDER BY Id LIMIT {Page}";
                q.Parameters.AddWithValue("$last", lastId);
                scope.Bind(q);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    page.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)));
                }
            }

            if (page.Count == 0)
            {
                break;
            }

            lastId = page[^1].Id;
            var results = new (FileKind Kind, string? Mime)[page.Count];
            Parallel.For(0, page.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ctx.Ct }, i =>
            {
                var (_, rootId, rel, ext, size) = page[i];
                var byExt = FileTypes.FromExtension(ext);
                if (byExt is { } t)
                {
                    results[i] = (t.Kind, t.Mime);
                    Interlocked.Increment(ref byExtension);
                    return;
                }

                if (sniff && size > 0 && rootPaths.TryGetValue(rootId, out var rootPath))
                {
                    var found = SniffFile(RootRegistry.Combine(rootPath, rel));
                    results[i] = found is { } s ? (s.Kind, s.Mime) : (FileKind.Other, "application/octet-stream");
                    if (found is null)
                    {
                        Interlocked.Increment(ref unknown);
                    }
                    else
                    {
                        Interlocked.Increment(ref sniffed);
                    }

                    return;
                }

                results[i] = (size == 0 ? FileKind.Other : FileKind.Unknown, null);
                Interlocked.Increment(ref unknown);
            });

            using var tx = db.BeginTransaction();
            update.Transaction = tx;
            for (var i = 0; i < page.Count; i++)
            {
                update.Parameters["$kind"].Value = (int)results[i].Kind;
                update.Parameters["$mime"].Value = (object?)results[i].Mime ?? DBNull.Value;
                update.Parameters["$id"].Value = page[i].Id;
                update.ExecuteNonQuery();
            }

            tx.Commit();
            ctx.Done += page.Count;
            ctx.Detected += page.Count;
            ctx.Current = page[^1].RelPath;
            ctx.Report();
            if (ctx.ProgressLineDue())
            {
                ctx.Log.LogInformation("Detecting file types: {Done} of {Total} done", ctx.Done, ctx.Total);
            }

            ctx.CheckYield();
        }

        if (ctx.Done > 0)
        {
            ctx.Log.LogInformation("Detected {Count} files: {Ext} by extension, {Sniffed} by content, {Unknown} unknown", ctx.Done, byExtension, sniffed, unknown);
        }
    }

    private static FileTypes.TypeInfo? SniffFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> buf = stackalloc byte[HeaderBytes];
            var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return FileTypes.Sniff(buf[..n]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long Count(SqliteConnection db, Scope scope)
    {
        using var q = db.CreateCommand();
        q.CommandText = $"SELECT COUNT(*) FROM Entries WHERE IsDir=0 AND Detected=0 {scope.Sql}";
        scope.Bind(q);
        return (long)q.ExecuteScalar()!;
    }
}

/// <summary>Restricts a phase to one root or a subtree of it; empty = everything.</summary>
public readonly record struct Scope(int? RootId, string? SubPath)
{
    public static readonly Scope Everything = new(null, null);

    public string Sql => RootId is null ? "" : string.IsNullOrEmpty(SubPath)
        ? "AND RootId = $scopeRoot"
        : "AND RootId = $scopeRoot AND (RelPath = $scopePath OR (RelPath >= $scopeFrom AND RelPath < $scopeTo))";

    public void Bind(SqliteCommand cmd)
    {
        if (RootId is null)
        {
            return;
        }

        cmd.Parameters.AddWithValue("$scopeRoot", RootId.Value);
        if (!string.IsNullOrEmpty(SubPath))
        {
            cmd.Parameters.AddWithValue("$scopePath", SubPath);
            cmd.Parameters.AddWithValue("$scopeFrom", SubPath + "/");
            cmd.Parameters.AddWithValue("$scopeTo", SubPath + "0");
        }
    }
}
