using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ANalyze.Infrastructure.Data;

/// <summary>Connection string and per-connection PRAGMAs shared by EF Core and the raw bulk paths.</summary>
public sealed class SqliteSetup(string databasePath)
{
    public string ConnectionString { get; } = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = true,
        DefaultTimeout = 60,
    }.ToString();

    internal const string Pragmas = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA busy_timeout=60000; PRAGMA foreign_keys=ON;";

    /// <summary>Opens a raw connection for bulk work (scanner, aggregates).</summary>
    public SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>WAL mode is persistent in the file, so it is set once at startup.</summary>
    public void EnableWal()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }
}

internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SqliteSetup.Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqliteSetup.Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
