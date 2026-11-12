using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Integrations.Items;
using ANalyze.Scanner;

namespace ANalyze.Integrations.Backup;

/// <summary>
/// The parent's copies as a source for "replace with the backup's copy" plans (<see cref="IBackupSource"/>): what the last comparison
/// knows about each file, and the copy itself streamed from the parent's <c>/backup/content</c>.
/// </summary>
internal sealed class BackupRestoreSource(IntegrationHost host, ParentIntegration plugin, BackupCompare compare, RootRegistry roots, SqliteSetup sqlite)
    : IBackupSource
{
    public async Task<IReadOnlyDictionary<long, BackupCopy>> FindAsync(IReadOnlyCollection<long> entryIds, CancellationToken ct)
    {
        var found = new Dictionary<long, BackupCopy>();
        if (entryIds.Count == 0 || !(await host.LoadAsync(plugin.Id, ct)).Enabled || await plugin.LoadSnapshotAsync(ct) is not { } snap)
        {
            return found;
        }

        using var c = sqlite.Open();
        foreach (var chunk in entryIds.Distinct().Chunk(500))
        {
            // Ids are numbers, so they can go into the statement as they are.
            var rows = Db.Query(c, $"""
                SELECT b.EntryId, b.LocalRootId, b.LocalRelPath, b.ParentSize, b.ParentHash, b.State, b.Verified, e.CorruptAt IS NOT NULL
                FROM BackupFiles b JOIN Entries e ON e.Id = b.EntryId
                WHERE b.IsDir = 0 AND b.ParentSize IS NOT NULL AND b.EntryId IN ({string.Join(',', chunk)})
                """, r => (Id: r.GetInt64(0), Root: r.GetInt32(1), Rel: r.GetString(2), Size: r.GetInt64(3), Hash: r.IsDBNull(4) ? null : r.GetString(4),
                    State: r.GetString(5), Verified: r.GetBoolean(6), Flagged: r.GetBoolean(7)));
            foreach (var x in rows)
            {
                var location = $"{snap.ParentName}:{BackupEndpoints.ParentPath(roots, snap, x.Root, x.Rel) ?? x.Rel}";
                found[x.Id] = new BackupCopy(location, x.Size, x.Hash, x.State == BackupStates.Corrupt || x.Flagged, x.Verified && !x.Flagged);
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<long>> DamagedAsync(CancellationToken ct)
    {
        if (!(await host.LoadAsync(plugin.Id, ct)).Enabled)
        {
            return [];
        }

        using var c = sqlite.Open();
        return Db.Query(c, $"""
            SELECT b.EntryId FROM BackupFiles b JOIN Entries e ON e.Id = b.EntryId
            WHERE b.IsDir = 0 AND b.ParentSize IS NOT NULL AND (b.State = '{BackupStates.Corrupt}' OR e.CorruptAt IS NOT NULL)
            ORDER BY b.EntryId
            """, r => r.GetInt64(0));
    }

    public async Task<Stream> OpenAsync(long entryId, CancellationToken ct)
    {
        HttpResponseMessage? res;
        BackupCompare.ParentFile f;
        try
        {
            f = await compare.LocateAsync(entryId, ct);
            res = await f.Client.ContentAsync(f.ParentRootId, f.ParentRel, null, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or UriFormatException
            || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new IOException(e.Message, e);
        }

        if (res is null)
        {
            throw new IOException($"{f.ParentName} no longer has this file ({f.ParentPath}).");
        }

        try
        {
            return new ResponseStream(res, await res.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            res.Dispose();
            throw;
        }
    }

    /// <summary>The body of a response that disposes the response with it; network errors while reading surface as <see cref="IOException"/>.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Wrap(() => inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken);
            }
            catch (HttpRequestException e)
            {
                throw new IOException(e.Message, e);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        private static int Wrap(Func<int> read)
        {
            try
            {
                return read();
            }
            catch (HttpRequestException e)
            {
                throw new IOException(e.Message, e);
            }
        }
    }
}
