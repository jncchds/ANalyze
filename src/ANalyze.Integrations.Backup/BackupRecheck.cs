using System.Text.Json.Nodes;
using ANalyze.Scanner;
using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Backup;

/// <summary>
/// Re-checks backed-up files against the parent's copy without a full sync, when a content hash arrives on either side: a file hashed
/// here (<see cref="IHashListener"/>, after each batch of the background hashing) or a hash the parent computed since the last sync
/// (<see cref="BackupHashRefresh"/>). Same rules as <see cref="BackupSync"/>: equal hashes verify the file; a different size or hash
/// inside a synchronized folder makes it corrupt. Folder states are left to the next full sync (hashes do not change them).
/// </summary>
internal static class BackupRecheck
{
    /// <summary>Re-evaluates the files with these entry ids that the parent has; returns how many are corrupt now.</summary>
    public static int Run(SqliteConnection conn, IReadOnlyCollection<long> entryIds)
    {
        if (entryIds.Count == 0)
        {
            return 0;
        }

        var corrupt = new List<(long Id, long Size, long ParentSize)>();
        var checkedIds = new List<long>();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "CREATE TEMP TABLE IF NOT EXISTS RecheckIds (Id INTEGER PRIMARY KEY); DELETE FROM temp.RecheckIds;");
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO temp.RecheckIds VALUES ($id)";
            var p = ins.Parameters.Add(new SqliteParameter("$id", null));
            foreach (var id in entryIds)
            {
                p.Value = id;
                ins.ExecuteNonQuery();
            }
        }

        Exec(conn, tx, $"""
            UPDATE BackupFiles SET
                Size = e.Size,
                Verified = e.FullHash IS NOT NULL AND e.FullHash = BackupFiles.ParentHash,
                State = CASE
                    WHEN (e.Size <> BackupFiles.ParentSize
                          OR (e.FullHash IS NOT NULL AND BackupFiles.ParentHash IS NOT NULL AND e.FullHash <> BackupFiles.ParentHash))
                         AND EXISTS (SELECT 1 FROM BackupFiles f WHERE f.EntryId = e.ParentId AND f.IsDir = 1 AND f.State = '{BackupStates.Synced}')
                    THEN '{BackupStates.Corrupt}' ELSE '{BackupStates.Synced}' END
            FROM Entries e
            WHERE e.Id = BackupFiles.EntryId AND BackupFiles.IsDir = 0 AND BackupFiles.ParentSize IS NOT NULL
              AND BackupFiles.EntryId IN (SELECT Id FROM temp.RecheckIds)
            """);

        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT EntryId, State, Size, ParentSize FROM BackupFiles
                WHERE IsDir = 0 AND ParentSize IS NOT NULL AND EntryId IN (SELECT Id FROM temp.RecheckIds)
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                checkedIds.Add(r.GetInt64(0));
                if (r.GetString(1) == BackupStates.Corrupt)
                {
                    corrupt.Add((r.GetInt64(0), r.GetInt64(2), r.GetInt64(3)));
                }
            }
        }

        if (checkedIds.Count > 0)
        {
            // The file labels follow: a corrupt file gets one, a synchronized one none.
            Exec(conn, tx, $"""
                DELETE FROM Annotations WHERE Source = '{ParentIntegration.IntegrationId}' AND Kind = '{BackupAnnotations.Kind}'
                  AND EntryId IN (SELECT Id FROM temp.RecheckIds)
                  AND EntryId IN (SELECT EntryId FROM BackupFiles WHERE IsDir = 0 AND ParentSize IS NOT NULL)
                """);
            if (corrupt.Count > 0)
            {
                var parentName = ParentName(conn, tx);
                using var ann = conn.CreateCommand();
                ann.Transaction = tx;
                ann.CommandText = "INSERT INTO Annotations (EntryId, Source, Kind, Label, Detail, State) VALUES ($e, $s, $k, $l, $d, $st)";
                ann.Parameters.AddWithValue("$s", ParentIntegration.IntegrationId);
                ann.Parameters.AddWithValue("$k", BackupAnnotations.Kind);
                ann.Parameters.AddWithValue("$l", BackupAnnotations.CorruptLabel);
                ann.Parameters.AddWithValue("$st", BackupStates.Corrupt);
                var e = ann.Parameters.Add(new SqliteParameter("$e", null));
                var d = ann.Parameters.Add(new SqliteParameter("$d", null));
                foreach (var (id, size, parentSize) in corrupt)
                {
                    e.Value = id;
                    d.Value = BackupAnnotations.CorruptDetail(parentName, size, parentSize);
                    ann.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
        return corrupt.Count;
    }

    private static string ParentName(SqliteConnection conn, SqliteTransaction tx)
    {
        using var q = conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT Value FROM Settings WHERE Key = $k";
        q.Parameters.AddWithValue("$k", ParentIntegration.SnapshotKey);
        try
        {
            return q.ExecuteScalar() is string json ? JsonNode.Parse(json)?["parentName"]?.GetValue<string>() ?? "the parent" : "the parent";
        }
        catch (System.Text.Json.JsonException)
        {
            return "the parent";
        }
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
}

/// <summary>Re-checks backed-up files as soon as the background hashing stores their hashes.</summary>
internal sealed class BackupHashListener : IHashListener
{
    public void OnHashed(SqliteConnection conn, IReadOnlyList<long> entryIds) => BackupRecheck.Run(conn, entryIds);
}
