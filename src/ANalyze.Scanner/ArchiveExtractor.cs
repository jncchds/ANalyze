using System.IO.Hashing;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace ANalyze.Scanner;

/// <summary>What extracting one archive member wrote: its size, CRC32, content hash (as <see cref="Hashing.FullHashAsync"/>) and the archive's date for it.</summary>
public sealed record ExtractedMember(long Size, uint Crc, string Hash, DateTime? Modified);

/// <summary>
/// Extracts single files of archives for plans (<c>ActionOp.Extract</c>), matching members by their normalized path
/// (<see cref="ArchiveIndexer.Normalize"/>). zip, rar and 7z are opened once and kept open while consecutive files come from the same
/// archive; tar and single-file compressed archives have no index, so the first file asked of one decompresses it once and writes every
/// file announced with <see cref="Want"/> to its own temporary file on the way.
/// </summary>
public sealed class ArchiveExtractor : IDisposable
{
    private readonly Dictionary<string, Dictionary<string, string>> _wanted = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Archive, string Member), (string Tmp, ExtractedMember Result)> _prepared = new();
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private string? _openPath;
    private IArchive? _open;

    /// <summary>Announces a member that will be asked for, with the temporary file a streamed archive's single pass writes it to.</summary>
    public void Want(string archivePath, string memberPath, string tmpPath)
    {
        if (!_wanted.TryGetValue(archivePath, out var members))
        {
            _wanted[archivePath] = members = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        members.TryAdd(memberPath, tmpPath);
    }

    /// <summary>
    /// Writes the member to <paramref name="tmpPath"/> (created new). Throws <see cref="FileNotFoundException"/> when the archive does not
    /// have it, <see cref="IOException"/> or the archive library's errors when it cannot be read.
    /// </summary>
    public async Task<ExtractedMember> ExtractAsync(string archivePath, string archiveName, string memberPath, string tmpPath, CancellationToken ct)
    {
        var ext = Path.GetExtension(archiveName).TrimStart('.').ToLowerInvariant();
        if (ext == "gz" && archiveName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ext = "tgz";
        }

        if (!ArchiveIndexer.IsStreamed(ext))
        {
            return await FromIndexedAsync(archivePath, memberPath, tmpPath, ct);
        }

        if (!_passed.Contains(archivePath))
        {
            Want(archivePath, memberPath, tmpPath);
            await PassAsync(archivePath, archiveName, ct);
        }

        if (!_prepared.Remove((archivePath, memberPath), out var p))
        {
            throw new FileNotFoundException("The archive does not have this file (any more)");
        }

        if (p.Tmp != tmpPath)
        {
            File.Move(p.Tmp, tmpPath);
        }

        return p.Result;
    }

    private async Task<ExtractedMember> FromIndexedAsync(string archivePath, string memberPath, string tmpPath, CancellationToken ct)
    {
        if (_openPath != archivePath)
        {
            _open?.Dispose();
            (_open, _openPath) = (null, null);
            _open = ArchiveFactory.OpenArchive(archivePath);
            _openPath = archivePath;
        }

        var entry = _open!.Entries.FirstOrDefault(e => !e.IsDirectory && string.IsNullOrEmpty(e.LinkTarget) && ArchiveIndexer.Normalize(e.Key) == memberPath)
            ?? throw new FileNotFoundException("The archive does not have this file (any more)");
        await using var input = entry.OpenEntryStream();
        return await CopyAsync(input, tmpPath, entry.LastModifiedTime, ct);
    }

    /// <summary>One pass over a streamed archive, writing every wanted member to its temporary file.</summary>
    private async Task PassAsync(string archivePath, string archiveName, CancellationToken ct)
    {
        _passed.Add(archivePath);
        var wanted = _wanted.GetValueOrDefault(archivePath) ?? [];
        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = ReaderFactory.OpenReader(fs, new ReaderOptions { LeaveStreamOpen = true });
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var e = reader.Entry;
            if (e.IsDirectory || !string.IsNullOrEmpty(e.LinkTarget))
            {
                continue;
            }

            var p = ArchiveIndexer.Normalize(string.IsNullOrEmpty(e.Key) ? Path.GetFileNameWithoutExtension(archiveName) : e.Key);
            if (p is null || !wanted.TryGetValue(p, out var tmp) || _prepared.ContainsKey((archivePath, p)))
            {
                continue;
            }

            await using var input = reader.OpenEntryStream();
            _prepared[(archivePath, p)] = (tmp, await CopyAsync(input, tmp, e.LastModifiedTime, ct));
        }
    }

    private static async Task<ExtractedMember> CopyAsync(Stream input, string tmpPath, DateTime? modified, CancellationToken ct)
    {
        var crc = new Crc32();
        var hash = new XxHash128();
        long size = 0;
        await using (var output = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await input.ReadAsync(buf, ct)) > 0)
            {
                crc.Append(buf.AsSpan(0, n));
                hash.Append(buf.AsSpan(0, n));
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                size += n;
            }

            await output.FlushAsync(ct);
        }

        return new ExtractedMember(size, crc.GetCurrentHashAsUInt32(), Convert.ToHexStringLower(hash.GetCurrentHash()), modified);
    }

    /// <summary>Closes the open archive and deletes temporary files a streamed pass wrote but nobody took.</summary>
    public void Dispose()
    {
        _open?.Dispose();
        (_open, _openPath) = (null, null);
        foreach (var (tmp, _) in _prepared.Values)
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }

        _prepared.Clear();
    }
}
