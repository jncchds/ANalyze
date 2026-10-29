using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Infrastructure.Data;
using ANalyze.Scanner;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

public class ArchiveTests
{
    private static byte[] Bytes(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void Zip(string path, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(content);
        }
    }

    private static void TarGz(string path, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (name, content) in files)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
        }
    }

    private static async Task<JsonElement> Extractions(HttpClient http, string query = "") =>
        await http.GetFromJsonAsync<JsonElement>("/api/v1/archives/extractions?limit=100" + query);

    private static JsonElement ByArchive(JsonElement list, string archiveRelPath) =>
        list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("archive").GetProperty("relPath").GetString() == archiveRelPath);

    [Fact]
    public void Archive_paths_are_normalized_and_junk_is_dropped()
    {
        Assert.Equal("a/b.txt", ArchiveIndexer.Normalize(@".\a\\b.txt"));
        Assert.Null(ArchiveIndexer.Normalize("__MACOSX/a/._b.txt"));
        Assert.Null(ArchiveIndexer.Normalize("a/.DS_Store"));
        Assert.Null(ArchiveIndexer.Normalize("../evil.txt"));
    }

    [Fact]
    public async Task Finds_complete_partial_and_unwrapped_extractions_and_checks_them_against_the_archive()
    {
        await using var app = new TestApp();
        var lib = app.Library;
        var (a, b, c) = (Bytes(50_000, 1), Bytes(20_000, 2), Bytes(300, 3));
        Zip(Path.Combine(lib, "Downloads", "pack.zip"), ("pack/a.bin", a), ("pack/sub/b.bin", b), ("pack/c.txt", c));
        Write(Path.Combine(lib, "Extracted", "pack", "a.bin"), a);
        Write(Path.Combine(lib, "Extracted", "pack", "sub", "b.bin"), b);
        Write(Path.Combine(lib, "Extracted", "pack", "c.txt"), c);
        Write(Path.Combine(lib, "Extracted", "pack", "my-notes.txt"), Bytes(100, 4)); // extra files do not matter

        // Interrupted: x complete, y cut short, z never written.
        var (x, y, z) = (Bytes(1_500_000, 5), Bytes(200_000, 6), Bytes(90_000, 7));
        Zip(Path.Combine(lib, "Downloads", "two.zip"), ("x.bin", x), ("y.bin", y), ("z.bin", z));
        Write(Path.Combine(lib, "Two", "x.bin"), x);
        Write(Path.Combine(lib, "Two", "y.bin"), y[..50_000]);

        // Extracted without the archive's top folder.
        var (one, two) = (Bytes(40_000, 8), Bytes(30_000, 9));
        Zip(Path.Combine(lib, "Downloads", "wrapped.zip"), ("wrapped/one.bin", one), ("wrapped/two.bin", two));
        Write(Path.Combine(lib, "W", "one.bin"), one);
        Write(Path.Combine(lib, "W", "two.bin"), two);

        // An archive nobody extracted.
        Zip(Path.Combine(lib, "Downloads", "lonely.zip"), ("nothing.bin", Bytes(10_000, 10)));

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var list = await Extractions(http);

        var pack = ByArchive(list, "Downloads/pack.zip");
        Assert.Equal("complete", pack.GetProperty("state").GetString());
        Assert.Equal("Extracted/pack", pack.GetProperty("folder").GetProperty("relPath").GetString());
        Assert.Equal(3, pack.GetProperty("matched").GetInt32());

        var partial = ByArchive(list, "Downloads/two.zip");
        Assert.Equal("partial", partial.GetProperty("state").GetString());
        Assert.Equal(1, partial.GetProperty("matched").GetInt32());
        Assert.Equal(1, partial.GetProperty("smaller").GetInt32());
        Assert.Equal(1, partial.GetProperty("missing").GetInt32());

        var wrapped = ByArchive(list, "Downloads/wrapped.zip");
        Assert.Equal("complete", wrapped.GetProperty("state").GetString());
        Assert.Equal("W", wrapped.GetProperty("baseRelPath").GetString());
        Assert.Equal("wrapped/", wrapped.GetProperty("strip").GetString());

        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("archive").GetProperty("relPath").GetString() == "Downloads/lonely.zip");

        // The per-file view names what is wrong with the partial extraction.
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/archives/extractions/{partial.GetProperty("id").GetInt64()}?problems=true");
        var states = detail.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("state").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["y.bin"] = "smaller", ["z.bin"] = "missing" }, states);

        // Damage one extracted file without changing its size or date, then check against the archive's CRC32s.
        var damaged = Path.Combine(lib, "Extracted", "pack", "sub", "b.bin");
        var when = File.GetLastWriteTimeUtc(damaged);
        var bad = (byte[])b.Clone();
        bad[100] ^= 0xFF;
        File.WriteAllBytes(damaged, bad);
        File.SetLastWriteTimeUtc(damaged, when);
        var ids = new[] { pack.GetProperty("id").GetInt64(), wrapped.GetProperty("id").GetInt64() };
        (await http.PostAsJsonAsync("/api/v1/archives/extractions/verify", new { ids })).EnsureSuccessStatusCode();
        await WaitAsync(async () => (await Extractions(http, "&state=complete")).GetProperty("items").EnumerateArray().All(i => i.TryGetProperty("verifiedAt", out var v) && v.ValueKind == JsonValueKind.Number));

        list = await Extractions(http);
        pack = ByArchive(list, "Downloads/pack.zip");
        Assert.Equal(1, pack.GetProperty("verifiedBad").GetInt32());
        Assert.Equal(2, pack.GetProperty("verifiedOk").GetInt32());
        wrapped = ByArchive(list, "Downloads/wrapped.zip");
        Assert.Equal(2, wrapped.GetProperty("verifiedOk").GetInt32());
        detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/archives/extractions/{pack.GetProperty("id").GetInt64()}?problems=true");
        Assert.Equal("corrupt", detail.GetProperty("files").EnumerateArray().Single().GetProperty("state").GetString());

        // A rescan keeps the result while the files stay the same; the insight lists only the archive whose copy checked out.
        await app.ScanAsync(http);
        Assert.Equal(1, ByArchive(await Extractions(http), "Downloads/pack.zip").GetProperty("verifiedBad").GetInt32());
        var insights = await http.GetFromJsonAsync<JsonElement>("/api/v1/insights");
        var verified = insights.EnumerateArray().Single(i => i.GetProperty("key").GetString() == "extracted-verified");
        Assert.Equal(1, verified.GetProperty("count").GetInt64());

        // Plan to remove the archive that is fully extracted.
        var plan = await http.PostAsJsonAsync("/api/v1/archives/extractions/plan", new { ids = new[] { wrapped.GetProperty("id").GetInt64() }, remove = "archive", op = "trash" });
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        Assert.Equal(1, (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetInt32());
        // A partial extraction's archive is kept.
        var refused = await http.PostAsJsonAsync("/api/v1/archives/extractions/plan", new { ids = new[] { partial.GetProperty("id").GetInt64() }, remove = "archive", op = "trash" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Tar_archives_are_listed_only_when_enabled()
    {
        await using var app = new TestApp();
        var (a, b) = (Bytes(60_000, 11), Bytes(70_000, 12));
        TarGz(Path.Combine(app.Library, "Downloads", "bundle.tar.gz"), ("bundle/a.bin", a), ("bundle/b.bin", b));
        Write(Path.Combine(app.Library, "Out", "bundle", "a.bin"), a);
        Write(Path.Combine(app.Library, "Out", "bundle", "b.bin"), b);

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        Assert.Equal(0, (await Extractions(http)).GetProperty("total").GetInt64());

        var settings = await http.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings.GetRawText())!;
        body["listStreamedArchives"] = JsonSerializer.SerializeToElement(true);
        (await http.PutAsJsonAsync("/api/v1/settings", body)).EnsureSuccessStatusCode();
        await app.ScanAsync(http);

        var x = ByArchive(await Extractions(http), "Downloads/bundle.tar.gz");
        Assert.Equal("complete", x.GetProperty("state").GetString());
        Assert.Equal("Out/bundle", x.GetProperty("folder").GetProperty("relPath").GetString());

        // CRC32s computed while listing make the check work without reading the archive again.
        (await http.PostAsJsonAsync("/api/v1/archives/extractions/verify", new { ids = new[] { x.GetProperty("id").GetInt64() } })).EnsureSuccessStatusCode();
        await WaitAsync(async () => ByArchive(await Extractions(http), "Downloads/bundle.tar.gz").GetProperty("verifiedOk").GetInt32() == 2);
    }

    [Fact]
    public async Task Ignoring_a_folder_removes_it_from_results_at_once()
    {
        await using var app = new TestApp();
        var content = Bytes(5000, 13);
        Write(Path.Combine(app.Library, "Keep", "photo.jpg"), content);
        Write(Path.Combine(app.Library, "Snapshots", "old", "photo.jpg"), content);
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        Assert.Contains("Snapshots/old/photo.jpg", await DuplicatePaths(http));

        (await http.PostAsJsonAsync("/api/v1/ignore", new { rootId = 1, relPath = "Snapshots" })).EnsureSuccessStatusCode();
        await WaitAsync(async () => !(await DuplicatePaths(http)).Contains("Snapshots/old/photo.jpg"));

        using var c = app.Services.GetRequiredService<SqliteSetup>().Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM Entries WHERE RelPath = 'Snapshots' OR RelPath LIKE 'Snapshots/%'";
        Assert.Equal(0L, (long)q.ExecuteScalar()!);
        q.CommandText = "SELECT TotalSize FROM Entries WHERE RelPath = ''";
        var rootSize = (long)q.ExecuteScalar()!;
        q.CommandText = "SELECT COALESCE(SUM(TotalSize), 0) FROM Entries WHERE Depth = 1";
        Assert.Equal((long)q.ExecuteScalar()!, rootSize);
    }

    private static async Task<List<string>> DuplicatePaths(HttpClient http)
    {
        var d = await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates?limit=500");
        return [.. d.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("members").EnumerateArray()).Select(m => m.GetProperty("relPath").GetString()!)];
    }

    private static async Task WaitAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await done())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition not reached");
    }
}
