using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ANalyze.Tests;

public class DatabaseTests
{
    [Theory]
    [InlineData(null, DbProvider.Sqlite)]
    [InlineData("SQLite", DbProvider.Sqlite)]
    [InlineData("postgres", DbProvider.Postgres)]
    [InlineData(" PostgreSQL ", DbProvider.Postgres)]
    public void Provider_names_are_parsed(string? value, DbProvider expected) => Assert.Equal(expected, DbSetup.ParseProvider(value));

    [Fact]
    public void Postgres_needs_a_connection_string_and_unknown_providers_fail()
    {
        Assert.Throws<InvalidOperationException>(() => DbSetup.ParseProvider("mysql"));
        Assert.Throws<InvalidOperationException>(() => DbSetup.FromOptions(new AnalyzeOptions { Database = new DatabaseOptions { Provider = "postgres" } }));
        var pg = DbSetup.FromOptions(new AnalyzeOptions { Database = new DatabaseOptions { Provider = "postgres", ConnectionString = "Host=db;Database=analyze;Username=u;Password=secret" } });
        Assert.True(pg.IsPostgres);
        Assert.Equal("db/analyze", pg.Describe());
        Assert.Contains("Command Timeout=0", pg.ConnectionString);
        var sqlite = DbSetup.FromOptions(new AnalyzeOptions { DataDir = "/tmp/x" });
        Assert.False(sqlite.IsPostgres);
        Assert.Equal("/tmp/x/analyze.db", sqlite.Describe());
    }

    /// <summary>Needs ANALYZE_TEST_POSTGRES (see <see cref="TestApp.Postgres"/>); passes without doing anything otherwise.</summary>
    [Fact]
    public async Task Sqlite_database_is_copied_into_postgres()
    {
        if (TestApp.Postgres is null)
        {
            return;
        }

        await using var app = new TestApp { UseSqlite = true };
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        (await http.PostAsJsonAsync("/api/v1/plans/dedupe", new { keep = "preferredFolder", op = "trash", preferredFolders = new[] { "Photos" } }, TestApp.Json))
            .EnsureSuccessStatusCode();

        var name = TestApp.CreatePgDatabase();
        try
        {
            var target = new DbSetup(DbProvider.Postgres, TestApp.PgConnection(name));
            await using (var db = target.CreateContext(target.CreateOptions()))
            {
                await db.Database.MigrateAsync();
            }

            var source = DbSetup.Sqlite(Path.Combine(app.Data, "analyze.db"));
            var result = await SqliteImporter.RunAsync(Path.Combine(app.Data, "analyze.db"), target, force: false, NullLogger.Instance);
            Assert.True(result.Rows > 0);

            // Every table holds the same rows.
            await using (var from = source.CreateContext(source.CreateOptions()))
            await using (var to = target.CreateContext(target.CreateOptions()))
            {
                Assert.Equal(await from.Entries.CountAsync(), await to.Entries.CountAsync());
                Assert.Equal(await from.Entries.SumAsync(e => e.Size), await to.Entries.SumAsync(e => e.Size));
                Assert.Equal(await from.Entries.CountAsync(e => e.IsDir), await to.Entries.CountAsync(e => e.IsDir));
                Assert.Equal(await from.Entries.CountAsync(e => e.FullHash != null), await to.Entries.CountAsync(e => e.FullHash != null));
                Assert.Equal(await from.Categories.CountAsync(), await to.Categories.CountAsync());
                Assert.Equal(await from.Rules.CountAsync(), await to.Rules.CountAsync());
                Assert.Equal(await from.Scans.CountAsync(), await to.Scans.CountAsync());
                Assert.Equal(await from.ActionItems.CountAsync(), await to.ActionItems.CountAsync());
                Assert.Equal(await from.Settings.CountAsync(), await to.Settings.CountAsync());
                var rule = await from.Rules.OrderBy(r => r.Id).FirstAsync();
                Assert.Equal(JsonSerializer.Serialize(rule.Condition), JsonSerializer.Serialize((await to.Rules.SingleAsync(r => r.Id == rule.Id)).Condition));

                // Ids continue after the copied ones.
                var maxId = await to.Categories.MaxAsync(c => c.Id);
                var added = new Category { Name = "Imported later", Color = "#123456" };
                to.Categories.Add(added);
                await to.SaveChangesAsync();
                Assert.True(added.Id > maxId);
            }

            // A database with scans in it is not replaced without force.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteImporter.RunAsync(Path.Combine(app.Data, "analyze.db"), target, force: false, NullLogger.Instance));
            var again = await SqliteImporter.RunAsync(Path.Combine(app.Data, "analyze.db"), target, force: true, NullLogger.Instance);
            Assert.Equal(result.Rows, again.Rows);
        }
        finally
        {
            TestApp.DropPgDatabase(name);
        }
    }

    /// <summary>The app on PostgreSQL imports ANalyze__Database__ImportFrom at startup and works with the copied index.</summary>
    [Fact]
    public async Task Startup_import_brings_the_sqlite_index_along()
    {
        if (TestApp.Postgres is null)
        {
            return;
        }

        long entries;
        string sqlitePath;
        var keep = Path.Combine(Path.GetTempPath(), "analyze-import-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        await using (var sqliteApp = new TestApp { UseSqlite = true })
        {
            using var http = sqliteApp.CreateClient();
            await sqliteApp.ScanAsync(http);
            var roots = await http.GetFromJsonAsync<JsonElement>("/api/v1/roots");
            entries = roots[0].GetProperty("files").GetInt64();
            sqlitePath = Path.Combine(sqliteApp.Data, "analyze.db");
            // A consistent copy of the live database (WAL included).
            await using var c = DbSetup.Sqlite(sqlitePath).Open();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{keep}'";
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await using var app = new TestApp { ImportFrom = keep };
            using var http2 = app.CreateClient();
            // This app's library is another temp folder (a new root), so look at what does not depend on the root.
            var scans = await http2.GetFromJsonAsync<JsonElement>("/api/v1/scans?limit=5");
            Assert.Equal(entries, scans.EnumerateArray().Single().GetProperty("filesSeen").GetInt64());
            var dups = await http2.GetFromJsonAsync<JsonElement>("/api/v1/duplicates");
            Assert.True(dups.GetProperty("total").GetInt64() > 0);
            var config = await http2.GetFromJsonAsync<JsonElement>("/api/v1/config");
            Assert.Equal("postgres", config.GetProperty("database").GetString());
        }
        finally
        {
            File.Delete(keep);
        }
    }
}
