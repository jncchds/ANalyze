using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ANalyze.Infrastructure.Data;

public enum DbProvider
{
    Sqlite,
    Postgres,
}

/// <summary>
/// The database ANalyze stores its index in (SQLite file or PostgreSQL server): connection string, per-connection setup and the
/// <see cref="DbContextOptions"/> for EF Core. Raw bulk paths (scanner, aggregates) open connections with <see cref="Open"/> and write
/// SQL both providers understand (see <see cref="SqlDialect"/>).
/// </summary>
public sealed class DbSetup
{
    public DbSetup(DbProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = provider == DbProvider.Sqlite ? connectionString : PostgresDefaults(connectionString);
    }

    /// <summary>SQLite file at <paramref name="databasePath"/>.</summary>
    public static DbSetup Sqlite(string databasePath) => new(DbProvider.Sqlite, new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = true,
        DefaultTimeout = 60,
    }.ToString());

    /// <summary>From the startup options (<c>ANalyze__Database__Provider</c>, <c>ANalyze__Database__ConnectionString</c>).</summary>
    public static DbSetup FromOptions(AnalyzeOptions o)
    {
        var provider = ParseProvider(o.Database.Provider);
        if (provider == DbProvider.Sqlite)
        {
            return string.IsNullOrWhiteSpace(o.Database.ConnectionString) ? Sqlite(o.DatabasePath) : new(DbProvider.Sqlite, o.Database.ConnectionString);
        }

        if (string.IsNullOrWhiteSpace(o.Database.ConnectionString))
        {
            throw new InvalidOperationException(
                "ANalyze__Database__Provider is postgres but ANalyze__Database__ConnectionString is empty " +
                "(e.g. \"Host=db;Database=analyze;Username=analyze;Password=…\").");
        }

        return new(DbProvider.Postgres, o.Database.ConnectionString);
    }

    public static DbProvider ParseProvider(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "sqlite" or "sqlite3" => DbProvider.Sqlite,
        "postgres" or "postgresql" or "pg" or "npgsql" => DbProvider.Postgres,
        _ => throw new InvalidOperationException($"Unknown database provider \"{value}\": use sqlite or postgres."),
    };

    public DbProvider Provider { get; }

    public bool IsPostgres => Provider == DbProvider.Postgres;

    public string ConnectionString { get; }

    /// <summary>"sqlite" or "postgres" (shown in the UI).</summary>
    public string Name => IsPostgres ? "postgres" : "sqlite";

    /// <summary>Where the data is, for the log and the UI, without credentials.</summary>
    public string Describe()
    {
        if (!IsPostgres)
        {
            return new SqliteConnectionStringBuilder(ConnectionString).DataSource;
        }

        var b = new NpgsqlConnectionStringBuilder(ConnectionString);
        return $"{b.Host}{(b.Port is 5432 or 0 ? "" : ":" + b.Port)}/{b.Database}";
    }

    internal const string SqlitePragmas = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA busy_timeout=60000; PRAGMA foreign_keys=ON;";

    /// <summary>Opens a raw connection for bulk work (scanner, aggregates).</summary>
    public DbConnection Open()
    {
        if (IsPostgres)
        {
            var pg = new NpgsqlConnection(ConnectionString);
            pg.Open();
            return pg;
        }

        var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SqlitePragmas;
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>EF Core options for <see cref="AnalyzeDbContext"/> (SQLite) or <see cref="PostgresDbContext"/>.</summary>
    public DbContextOptions CreateOptions()
    {
        if (IsPostgres)
        {
            return new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(ConnectionString, x => x.CommandTimeout(120))
                .Options;
        }

        return new DbContextOptionsBuilder<AnalyzeDbContext>()
            .UseSqlite(ConnectionString, x => x.CommandTimeout(120))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
    }

    /// <summary>A new context of the right type for the provider (the factory behind <c>IDbContextFactory&lt;AnalyzeDbContext&gt;</c>).</summary>
    public AnalyzeDbContext CreateContext(DbContextOptions options) => IsPostgres
        ? new PostgresDbContext((DbContextOptions<PostgresDbContext>)options)
        : new AnalyzeDbContext((DbContextOptions<AnalyzeDbContext>)options);

    /// <summary>SQLite only: WAL mode is persistent in the file, so it is set once at startup.</summary>
    public void EnableWal()
    {
        if (IsPostgres)
        {
            return;
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Defaults for settings the connection string leaves out: no statement time limit (aggregates over millions of files can take
    /// minutes, like they may on SQLite), automatic preparation of the scanner's per-row statements, and an application name.
    /// </summary>
    private static string PostgresDefaults(string connectionString)
    {
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (!given.ContainsKey("Command Timeout") && !given.ContainsKey("CommandTimeout"))
        {
            b.CommandTimeout = 0;
        }

        if (!given.ContainsKey("Max Auto Prepare") && !given.ContainsKey("MaxAutoPrepare"))
        {
            b.MaxAutoPrepare = 64;
        }

        if (!given.ContainsKey("Application Name") && !given.ContainsKey("ApplicationName"))
        {
            b.ApplicationName = "ANalyze";
        }

        return b.ConnectionString;
    }
}

internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DbSetup.SqlitePragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = DbSetup.SqlitePragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary><c>IDbContextFactory&lt;AnalyzeDbContext&gt;</c> that creates the provider's context type.</summary>
internal sealed class AnalyzeDbContextFactory(DbSetup setup) : IDbContextFactory<AnalyzeDbContext>
{
    private readonly DbContextOptions _options = setup.CreateOptions();

    public AnalyzeDbContext CreateDbContext() => setup.CreateContext(_options);
}
