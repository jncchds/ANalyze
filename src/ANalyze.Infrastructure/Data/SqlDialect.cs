using System.Data.Common;
using Npgsql;

namespace ANalyze.Infrastructure.Data;

/// <summary>
/// The few SQL fragments that differ between SQLite and PostgreSQL. Everything else in the raw SQL is written so both run it:
/// <c>@name</c> parameters, <c>TRUE</c>/<c>FALSE</c> for booleans (never 0/1 or arithmetic on them), <c>ON CONFLICT</c> instead of
/// <c>INSERT OR …</c>, <c>RETURNING</c> instead of <c>last_insert_rowid()</c>, an alias on every subquery in FROM, aggregates
/// (not output aliases) in HAVING, unqualified temp tables, and <c>CAST(@p AS TEXT) IS NULL</c> when a parameter may be NULL
/// with nothing else to tell PostgreSQL its type. Table and column names are unquoted: the PostgreSQL model uses lower-case
/// names (<see cref="PostgresDbContext"/>), which is what unquoted identifiers fold to.
/// </summary>
public static class SqlDialect
{
    public static bool IsPostgres(this DbConnection c) => c is NpgsqlConnection;

    /// <summary>
    /// SQLite: read the table front to back instead of walking a low-selectivity index (<c>Entries e NOT INDEXED</c>).
    /// PostgreSQL's planner chooses a sequential scan for such aggregates by itself.
    /// </summary>
    public static string NotIndexed(this DbConnection c) => c.IsPostgres() ? "" : " NOT INDEXED";

    /// <summary>SQLite: <c>INDEXED BY name</c>; nothing on PostgreSQL.</summary>
    public static string IndexedBy(this DbConnection c, string index) => c.IsPostgres() ? "" : $" INDEXED BY {index}";

    /// <summary>
    /// A boolean literal in a WHERE that must match a partial index's filter: SQLite only uses the index (and <c>INDEXED BY</c>
    /// fails without it) when the terms are written as in the filter, <c>IsDir = 0</c>.
    /// </summary>
    public static string Bool(this DbConnection c, bool value) => c.IsPostgres() ? (value ? "TRUE" : "FALSE") : (value ? "1" : "0");

    /// <summary>Case-insensitive LIKE (SQLite's LIKE is case-insensitive for ASCII; PostgreSQL needs ILIKE).</summary>
    public static string Like(this DbConnection c) => c.IsPostgres() ? "ILIKE" : "LIKE";

    /// <summary>Comma-separated distinct values of a group.</summary>
    public static string GroupConcatDistinct(this DbConnection c, string expr) =>
        c.IsPostgres() ? $"string_agg(DISTINCT {expr}, ',')" : $"GROUP_CONCAT(DISTINCT {expr})";

    /// <summary>1-based position of <paramref name="needle"/> in <paramref name="haystack"/>, 0 when absent.</summary>
    public static string Instr(this DbConnection c, string haystack, string needle) =>
        c.IsPostgres() ? $"strpos({haystack}, {needle})" : $"instr({haystack}, {needle})";

    /// <summary>Integer key column of a temp table (SQLite's rowid alias; a 64-bit key on PostgreSQL).</summary>
    public static string IdKey(this DbConnection c) => c.IsPostgres() ? "BIGINT PRIMARY KEY" : "INTEGER PRIMARY KEY";

    /// <summary>Text column of a temp table: byte order on PostgreSQL, like the model's columns (joins need the same collation).</summary>
    public static string Text(this DbConnection c) => c.IsPostgres() ? "TEXT COLLATE \"C\"" : "TEXT";

    /// <summary>Adds a parameter (DBNull for null) and returns it, like <c>SqliteParameterCollection.AddWithValue</c>.</summary>
    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, object? value)
    {
        var p = parameters switch
        {
            NpgsqlParameterCollection => (DbParameter)new NpgsqlParameter(),
            _ => new Microsoft.Data.Sqlite.SqliteParameter(),
        };
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        parameters.Add(p);
        return p;
    }

    /// <summary>
    /// Prepares a reused statement on SQLite. On PostgreSQL the connection prepares frequently used statements by itself
    /// (<c>Max Auto Prepare</c>), and explicit preparation would fix parameter types while their values are still unset.
    /// </summary>
    public static void PrepareReused(this DbCommand cmd)
    {
        if (cmd.Connection is not NpgsqlConnection)
        {
            cmd.Prepare();
        }
    }
}
