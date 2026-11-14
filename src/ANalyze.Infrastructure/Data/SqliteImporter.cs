using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ANalyze.Infrastructure.Data;

/// <summary>
/// Copies an ANalyze SQLite database into PostgreSQL: brings the SQLite file up to the current schema (its migrations), then
/// copies every table of the model with binary COPY in one transaction (parents before children), and moves the id sequences
/// past the copied ids. The PostgreSQL database must be migrated already and either never scanned or <c>force</c> given:
/// whatever it holds is replaced.
/// </summary>
public static class SqliteImporter
{
    public sealed record Result(int Tables, long Rows, TimeSpan Took);

    /// <summary>True when the PostgreSQL database has never been scanned (only seeds, roots or settings), so importing loses nothing.</summary>
    public static async Task<bool> IsFreshAsync(DbSetup target, CancellationToken ct = default)
    {
        await using var c = (NpgsqlConnection)target.Open();
        await using var cmd = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM entries) AND NOT EXISTS (SELECT 1 FROM scans)", c);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<Result> RunAsync(string sqlitePath, DbSetup target, bool force, ILogger log, CancellationToken ct = default)
    {
        if (!target.IsPostgres)
        {
            throw new InvalidOperationException("Importing needs PostgreSQL as the database (ANalyze__Database__Provider=postgres).");
        }

        sqlitePath = Path.GetFullPath(sqlitePath);
        if (!File.Exists(sqlitePath))
        {
            throw new FileNotFoundException($"No SQLite database at {sqlitePath}", sqlitePath);
        }

        if (!force && !await IsFreshAsync(target, ct))
        {
            throw new InvalidOperationException(
                $"The PostgreSQL database {target.Describe()} already holds scanned data; importing would replace it. Use --force to replace it anyway.");
        }

        var started = Stopwatch.StartNew();
        var source = DbSetup.Sqlite(sqlitePath);
        await using var sourceDb = source.CreateContext(source.CreateOptions());
        var pending = (await sourceDb.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            log.LogInformation("Bringing {Path} up to date first ({Count} migrations)", sqlitePath, pending.Count);
            await sourceDb.Database.MigrateAsync(ct);
        }

        await using var targetDb = target.CreateContext(target.CreateOptions());
        var tables = Ordered(targetDb.Model);

        await using var src = (SqliteConnection)source.Open();
        await using var dst = (NpgsqlConnection)target.Open();
        await using var tx = await dst.BeginTransactionAsync(ct);
        await using (var truncate = new NpgsqlCommand(
            $"TRUNCATE {string.Join(", ", tables.Select(t => Quote(t.GetTableName()!)))} RESTART IDENTITY CASCADE", dst, tx))
        {
            await truncate.ExecuteNonQueryAsync(ct);
        }

        long total = 0;
        foreach (var table in tables)
        {
            var sourceType = sourceDb.Model.FindEntityType(table.ClrType)
                ?? throw new InvalidOperationException($"{table.ClrType.Name} is missing from the SQLite model");
            var columns = table.GetProperties()
                .Select(p => (Source: sourceType.FindProperty(p.Name)!.GetColumnName(), Target: p.GetColumnName(), Type: p.GetColumnType()))
                .ToList();
            var rows = await CopyTableAsync(src, dst, sourceType.GetTableName()!, table.GetTableName()!, columns, log, ct);
            total += rows;
            log.LogInformation("Imported {Table}: {Rows} rows", sourceType.GetTableName(), rows);
        }

        foreach (var table in tables)
        {
            foreach (var key in table.FindPrimaryKey()!.Properties.Where(p => p.ValueGenerated == ValueGenerated.OnAdd && p.ClrType is var t && (t == typeof(int) || t == typeof(long))))
            {
                var (name, col) = (table.GetTableName()!, key.GetColumnName());
                await using var seq = new NpgsqlCommand(
                    $"SELECT setval(pg_get_serial_sequence('{Quote(name)}', '{col}'), COALESCE(MAX({Quote(col)}), 1), MAX({Quote(col)}) IS NOT NULL) FROM {Quote(name)}",
                    dst, tx);
                await seq.ExecuteScalarAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        await using (var analyze = new NpgsqlCommand("ANALYZE", dst))
        {
            await analyze.ExecuteNonQueryAsync(ct);
        }

        log.LogInformation("Imported {Rows} rows from {Path} into {Target} in {Seconds:0.#} s", total, sqlitePath, target.Describe(), started.Elapsed.TotalSeconds);
        return new Result(tables.Count, total, started.Elapsed);
    }

    /// <summary>Tables of the model, principals before the tables that reference them.</summary>
    private static List<IEntityType> Ordered(IModel model)
    {
        var all = model.GetEntityTypes().Where(t => t.GetTableName() is not null && !t.IsOwned()).ToList();
        var done = new List<IEntityType>();
        void Visit(IEntityType t, HashSet<IEntityType> path)
        {
            if (done.Contains(t) || !path.Add(t))
            {
                return;
            }

            foreach (var fk in t.GetForeignKeys().Where(fk => fk.PrincipalEntityType != t))
            {
                Visit(fk.PrincipalEntityType, path);
            }

            done.Add(t);
        }

        foreach (var t in all)
        {
            Visit(t, []);
        }

        return done;
    }

    private static async Task<long> CopyTableAsync(SqliteConnection src, NpgsqlConnection dst, string sourceTable, string targetTable,
        List<(string Source, string Target, string Type)> columns, ILogger log, CancellationToken ct)
    {
        await using var read = src.CreateCommand();
        read.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Source)))} FROM {Quote(sourceTable)} ORDER BY rowid";
        try
        {
            await using var probe = src.CreateCommand();
            probe.CommandText = $"SELECT 1 FROM {Quote(sourceTable)} ORDER BY rowid LIMIT 1";
            await probe.ExecuteScalarAsync(ct);
        }
        catch (SqliteException)
        {
            // WITHOUT ROWID tables have no rowid; the order does not matter for them.
            read.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Source)))} FROM {Quote(sourceTable)}";
        }

        var types = columns.Select(c => c.Type.ToLowerInvariant()).ToArray();
        await using var writer = await dst.BeginBinaryImportAsync(
            $"COPY {Quote(targetTable)} ({string.Join(", ", columns.Select(c => Quote(c.Target)))}) FROM STDIN (FORMAT BINARY)", ct);
        await using var r = await read.ExecuteReaderAsync(ct);
        long rows = 0;
        var report = Stopwatch.StartNew();
        while (await r.ReadAsync(ct))
        {
            await writer.StartRowAsync(ct);
            for (var i = 0; i < types.Length; i++)
            {
                if (r.IsDBNull(i))
                {
                    await writer.WriteNullAsync(ct);
                    continue;
                }

                await WriteAsync(writer, r.GetValue(i), types[i], ct);
            }

            rows++;
            if (report.Elapsed > TimeSpan.FromSeconds(30))
            {
                log.LogInformation("Importing {Table}: {Rows} rows so far", sourceTable, rows);
                report.Restart();
            }
        }

        await writer.CompleteAsync(ct);
        return rows;
    }

    private static Task WriteAsync(NpgsqlBinaryImporter w, object v, string type, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        return type switch
        {
            "bigint" => w.WriteAsync(Convert.ToInt64(v, inv), type, ct),
            "integer" => w.WriteAsync(Convert.ToInt32(v, inv), type, ct),
            "smallint" => w.WriteAsync(Convert.ToInt16(v, inv), type, ct),
            "boolean" => w.WriteAsync(v is string s ? s is "1" or "true" or "True" : Convert.ToInt64(v, inv) != 0, type, ct),
            "double precision" => w.WriteAsync(Convert.ToDouble(v, inv), type, ct),
            "real" => w.WriteAsync(Convert.ToSingle(v, inv), type, ct),
            "bytea" => w.WriteAsync(v as byte[] ?? System.Text.Encoding.UTF8.GetBytes(Convert.ToString(v, inv) ?? ""), type, ct),
            "uuid" => w.WriteAsync(v is byte[] b ? new Guid(b) : Guid.Parse(Convert.ToString(v, inv)!), type, ct),
            _ when type.StartsWith("numeric", StringComparison.Ordinal) => w.WriteAsync(Convert.ToDecimal(v, inv), "numeric", ct),
            // PostgreSQL text cannot hold NUL characters (SQLite can).
            _ => w.WriteAsync((Convert.ToString(v, inv) ?? "").Replace("\0", "", StringComparison.Ordinal), "text", ct),
        };
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
