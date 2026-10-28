using Microsoft.Data.Sqlite;

namespace ANalyze.Scanner;

/// <summary>Small direct updates of the Entries table after actions, without a rescan.</summary>
public static class EntryMaintenance
{
    /// <summary>Deletes an entry with its subtree and refreshes the aggregates above it.</summary>
    public static void DeleteEntry(SqliteConnection conn, long entryId)
    {
        long? parentId;
        int rootId;
        string relPath;
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT ParentId, RootId, RelPath FROM Entries WHERE Id = $id";
            q.Parameters.AddWithValue("$id", entryId);
            using var r = q.ExecuteReader();
            if (!r.Read())
            {
                return;
            }

            parentId = r.IsDBNull(0) ? null : r.GetInt64(0);
            rootId = r.GetInt32(1);
            relPath = r.GetString(2);
        }

        using (var d = conn.CreateCommand())
        {
            d.CommandText = "DELETE FROM Entries WHERE Id = $id OR (RootId = $root AND RelPath >= $from AND RelPath < $to)";
            d.Parameters.AddWithValue("$id", entryId);
            d.Parameters.AddWithValue("$root", rootId);
            d.Parameters.AddWithValue("$from", relPath + "/");
            d.Parameters.AddWithValue("$to", relPath + "0");
            d.ExecuteNonQuery();
        }

        if (parentId is { } p)
        {
            RecomputeDir(conn, p);
        }
    }

    /// <summary>Recomputes one directory from its children, then every directory above it.</summary>
    public static void RecomputeDir(SqliteConnection conn, long dirId)
    {
        using (var u = conn.CreateCommand())
        {
            u.CommandText = """
                UPDATE Entries SET
                    TotalSize = COALESCE((SELECT SUM(TotalSize) FROM Entries c WHERE c.ParentId = $id), 0),
                    TotalAlloc = COALESCE((SELECT SUM(TotalAlloc) FROM Entries c WHERE c.ParentId = $id), 0),
                    FileCount = COALESCE((SELECT SUM(FileCount) FROM Entries c WHERE c.ParentId = $id), 0),
                    DirCount = COALESCE((SELECT SUM(DirCount + IsDir) FROM Entries c WHERE c.ParentId = $id), 0)
                WHERE Id = $id
                """;
            u.Parameters.AddWithValue("$id", dirId);
            u.ExecuteNonQuery();
        }

        ScanService.RecomputeAncestors(conn, dirId);
    }

    /// <summary>Refreshes inode/link data of a file after it was replaced by a hard link.</summary>
    public static void UpdateIdentity(SqliteConnection conn, long entryId, long inode, long dev, int nlink, long mtime)
    {
        using var u = conn.CreateCommand();
        u.CommandText = "UPDATE Entries SET Inode = $i, Dev = $d, NLink = $n, MTime = $m WHERE Id = $id";
        u.Parameters.AddWithValue("$i", inode);
        u.Parameters.AddWithValue("$d", dev);
        u.Parameters.AddWithValue("$n", nlink);
        u.Parameters.AddWithValue("$m", mtime);
        u.Parameters.AddWithValue("$id", entryId);
        u.ExecuteNonQuery();
    }
}
