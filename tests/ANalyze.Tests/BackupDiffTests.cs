using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Infrastructure.Settings;
using ANalyze.Integrations.Backup;
using ANalyze.Integrations.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

/// <summary>How a copy on the parent differs from the file here (<see cref="FileMap"/> + <see cref="BackupDiff"/>).</summary>
public sealed class BackupDiffTests : IDisposable
{
    private const int Block = 4096;
    private static readonly DateTime T0 = new(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("anz-diff-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] Data(int size, int seed = 1)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private async Task<FileMap> Map(string name, byte[] content, DateTime mtime, long compare)
    {
        var p = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(p, content);
        File.SetLastWriteTimeUtc(p, mtime);
        return await FileMap.ComputeAsync(p, Block, compare, null, CancellationToken.None);
    }

    private async Task<DiffReport> Diff(byte[] here, byte[] parent, DateTime? hereTime = null, DateTime? parentTime = null, DiffIndex? hereIndex = null, DiffIndex? parentIndex = null)
    {
        var min = Math.Min(here.Length, parent.Length);
        var a = await Map("here", here, hereTime ?? T0, min);
        // The parent's map goes over the wire.
        var b = FileMap.FromProbe((await Map("parent", parent, parentTime ?? T0, min)).ToProbe(null));
        return BackupDiff.Analyze(a, b, hereIndex ?? new DiffIndex(null, null, null), parentIndex ?? new DiffIndex(null, null, null), "nas1");
    }

    [Fact]
    public async Task Map_matches_the_index_hash_and_splits_blocks_from_both_ends()
    {
        var data = Data(10_000);
        var m = await Map("a", data, T0, data.Length);
        var p = Path.Combine(_dir, "a");
        Assert.Equal(await ANalyze.Scanner.Hashing.FullHashAsync(p, null, CancellationToken.None), m.Hash);
        Assert.Equal(3, m.Head.Length);
        Assert.Equal(3, m.Tail.Length);
        // Tail block 0 is the last 4096 bytes: equal to head block 1 of the same bytes shifted to a block boundary.
        var shifted = await Map("b", data[(10_000 - 2 * Block)..], T0, 0);
        Assert.Equal(shifted.Head[1], m.Tail[0]);
        Assert.Equal(shifted.Head[0], m.Tail[1]);
        Assert.Equal(new DateTimeOffset(T0).ToUnixTimeSeconds(), m.MTime);
        Assert.False(m.Changed);
    }

    [Fact]
    public async Task Same_content_means_outdated_index_data()
    {
        var d = Data(20_000);
        var r = await Diff(d, d, hereIndex: new DiffIndex("0123", 1, null));
        Assert.Equal(DiffVerdicts.Identical, r.Verdict);
        Assert.Contains(r.Details, x => x.Contains("outdated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shorter_backup_copy_is_truncated_or_outdated()
    {
        var d = Data(50_000);
        var cut = d[..30_001];
        var r = await Diff(d, cut);
        Assert.Equal(DiffVerdicts.Truncated, r.Verdict);
        Assert.Equal(BackupDiff.Parent, r.Damaged);
        Assert.Equal(30_001, r.CommonPrefix);

        // The file here is newer: it grew after the backup.
        r = await Diff(d, cut, hereTime: T0.AddHours(3));
        Assert.Equal(DiffVerdicts.Appended, r.Verdict);
        Assert.Equal(BackupDiff.Here, r.Newer);

        // Cut off here.
        r = await Diff(cut, d);
        Assert.Equal(DiffVerdicts.Truncated, r.Verdict);
        Assert.Equal(BackupDiff.Here, r.Damaged);
    }

    [Fact]
    public async Task Zero_filled_backup_copy()
    {
        var d = Data(64 * Block);
        var z = (byte[])d.Clone();
        Array.Clear(z, 20 * Block, 30 * Block);
        var r = await Diff(d, z);
        Assert.Equal(DiffVerdicts.Zeros, r.Verdict);
        Assert.Equal(BackupDiff.Parent, r.Damaged);
        Assert.Equal(30L * Block, r.DifferentBytes);
        Assert.True(Assert.Single(r.Ranges).ZeroParent);
    }

    [Fact]
    public async Task Few_changed_blocks_with_same_mtime_are_bit_rot()
    {
        var d = Data(100 * Block);
        var rot = (byte[])d.Clone();
        rot[37 * Block + 5] ^= 0x10;
        var r = await Diff(d, rot);
        Assert.Equal(DiffVerdicts.BitRot, r.Verdict);
        Assert.Equal(1, r.DifferentBlocks);
        Assert.Null(r.Damaged);

        // The parent noticed its copy change silently: the damage is there.
        r = await Diff(d, rot, parentIndex: new DiffIndex(null, null, 1_700_000_000));
        Assert.Equal(BackupDiff.Parent, r.Damaged);
    }

    [Fact]
    public async Task Rewritten_header_of_another_length_keeps_the_rest()
    {
        // Like an ID3 tag that grew: new bytes at the start, the audio after it unchanged.
        var audio = Data(80 * Block);
        var here = Data(3000, 7).Concat(audio).ToArray();
        var parent = Data(1200, 8).Concat(audio).ToArray();
        var r = await Diff(here, parent, hereTime: T0.AddDays(2));
        Assert.Equal(DiffVerdicts.HeaderChanged, r.Verdict);
        Assert.Equal(parent.Length, r.CommonSuffix + r.Ranges[0].ParentLength);
        Assert.Equal(BackupDiff.Here, r.Newer);
    }

    [Fact]
    public async Task Edited_in_the_middle_and_replaced_content()
    {
        var d = Data(100 * Block);
        var e = (byte[])d.Clone();
        Array.Copy(Data(10 * Block, 9), 0, e, 45 * Block, 10 * Block);
        var r = await Diff(d, e, hereTime: T0.AddMinutes(10));
        Assert.Equal(DiffVerdicts.Edited, r.Verdict);
        Assert.Equal(10, r.DifferentBlocks);
        Assert.Equal(BackupDiff.Parent, r.Damaged); // the older copy is the outdated one

        r = await Diff(d, Data(100 * Block, 2));
        Assert.Equal(DiffVerdicts.Different, r.Verdict);
    }

    [Fact]
    public async Task Parent_serves_a_probe_of_one_file()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        await settings.SaveAsync(settings.Current with { HashAllFiles = true });
        var data = Data(300_000);
        Directory.CreateDirectory(Path.Combine(app.Library, "docs"));
        await File.WriteAllBytesAsync(Path.Combine(app.Library, "docs/a.bin"), data);
        await app.ScanAsync(http);

        var body = await http.GetStringAsync("/api/v1/backup/probe?rootId=1&path=docs/a.bin&block=65536&compare=1000");
        var last = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        var probe = JsonSerializer.Deserialize<ProbeLine>(last, TestApp.Json)!.Result!;
        Assert.Equal(300_000, probe.Size);
        Assert.Equal(65536, probe.Block);
        Assert.Equal(1000, probe.Compare);
        Assert.Equal(probe.Hash, probe.Index!.Hash);
        Assert.Equal(5, FileMap.FromProbe(probe).Head.Length);

        var missing = await http.GetAsync("/api/v1/backup/probe?rootId=1&path=docs/nope.bin");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("missing", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var escape = await http.GetAsync("/api/v1/backup/probe?rootId=1&path=../../etc/passwd");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, escape.StatusCode);
    }

    [Fact]
    public async Task Child_compares_a_corrupt_file_with_the_parent()
    {
        // One instance plays both sides: the integration's HTTP client is routed back into the test server, and "bk" is the parent's copy.
        TestApp app = null!;
        app = new TestApp
        {
            ExtraServices = s => s.AddHttpClient(ANalyze.Integrations.IntegrationHttp.Name).ConfigurePrimaryHttpMessageHandler(() => app.Server.CreateHandler()),
        };
        await using var _ = app;
        using var http = app.CreateClient();
        var data = Data(300_000);
        Directory.CreateDirectory(Path.Combine(app.Library, "home/docs"));
        Directory.CreateDirectory(Path.Combine(app.Library, "bk/docs"));
        await File.WriteAllBytesAsync(Path.Combine(app.Library, "home/docs/v.bin"), data);
        await File.WriteAllBytesAsync(Path.Combine(app.Library, "bk/docs/v.bin"), data[..200_000]);
        await app.ScanAsync(http);
        (await http.PutAsJsonAsync("/api/v1/integrations/parent", new { enabled = false, syncAfterScan = false, values = new { url = "http://localhost" } })).EnsureSuccessStatusCode();

        // What a sync would have stored: the parent's root "/srv" is this library, its "bk" holds the copy of "home".
        long entryId;
        using (var conn = app.Services.GetRequiredService<ANalyze.Infrastructure.Data.SqliteSetup>().Open())
        {
            entryId = Db.Query(conn, "SELECT Id FROM Entries WHERE RelPath = 'home/docs/v.bin'", r => r.GetInt64(0)).Single();
            Db.Query(conn, """
                INSERT INTO BackupFiles (State, IsDir, EntryId, LocalRootId, LocalRelPath, Size, ParentSize, Verified, Children, SyncedChildren)
                VALUES ('corrupt', 0, $e, 1, 'home/docs/v.bin', 300000, 200000, 0, 0, 0) RETURNING Id
                """, r => r.GetInt64(0), ("$e", entryId));
        }

        var info = new ExportInfo("ANalyze", "test", "nas1", BackupExport.HashAlgorithm, true, 0, [new ExportRoot(1, "/srv", "srv", 1, 0, null, null)]);
        var snap = new BackupSnapshot(DateTimeOffset.UtcNow, "nas1", "http://localhost", info,
            [new BackupTarget(1, "/srv/bk", app.Library + "/home", 1, "home", null)], [new ANalyze.Integrations.PathMapping("/srv/bk", app.Library + "/home")], [], 0, true);
        var dbf = app.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ANalyze.Infrastructure.Data.AnalyzeDbContext>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.Settings.Add(new ANalyze.Domain.Setting { Key = ParentIntegration.SnapshotKey, Value = JsonSerializer.Serialize(snap, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            await db.SaveChangesAsync();
        }

        (await http.PostAsync($"/api/v1/integrations/parent/compare?entryId={entryId}", null)).EnsureSuccessStatusCode();
        JsonElement s = default;
        for (var i = 0; i < 100; i++)
        {
            s = await http.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/parent/compare?entryId={entryId}");
            if (s.GetProperty("status").GetString() != "running")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.Equal("done", s.GetProperty("status").GetString());
        var report = s.GetProperty("report");
        Assert.Equal(DiffVerdicts.Truncated, report.GetProperty("verdict").GetString());
        Assert.Equal(BackupDiff.Parent, report.GetProperty("damaged").GetString());
        Assert.Equal(200_000, report.GetProperty("commonPrefix").GetInt64());
    }
}
