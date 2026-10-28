using Microsoft.Data.Sqlite;

namespace ANalyze.Web.Api;

/// <summary>Tiny helpers for aggregate queries that EF Core cannot express efficiently.</summary>
public static class Sql
{
    public static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
        {
            list.Add(map(r));
        }

        return list;
    }

    public static T? Scalar<T>(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? default : (T)Convert.ChangeType(o, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static long? NullableLong(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static int? NullableInt(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static string? NullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>SQL fragment + parameters restricting Entries (alias e) to a root and folder.</summary>
    public static (string Where, (string, object?)[] Args) ScopeOf(int? rootId, string? path, string alias = "e")
    {
        if (rootId is null)
        {
            return ("", []);
        }

        var p = path?.Trim('/') ?? "";
        if (p.Length == 0)
        {
            return ($" AND {alias}.RootId = $root", [("$root", rootId)]);
        }

        return ($" AND {alias}.RootId = $root AND {alias}.RelPath >= $from AND {alias}.RelPath < $to", [("$root", rootId), ("$from", p + "/"), ("$to", p + "0")]);
    }

    public const string Identity = "CASE WHEN Inode = 0 THEN 'i' || Id ELSE Dev || ':' || Inode END";
}
