namespace ANalyze.Scanner;

/// <summary>What a backup knows about its copy of one local file.</summary>
/// <param name="Location">Where the copy is, for people ("backup-nas:/data/Photos/a.jpg").</param>
/// <param name="Size">Size of the copy at the last comparison.</param>
/// <param name="Hash">Content hash of the copy (same algorithm as <see cref="Hashing.FullHashAsync"/>), when the backup has one.</param>
/// <param name="Corrupt">The comparison found the local file damaged (other size or content than the copy).</param>
/// <param name="Identical">Both sides have the same content hash.</param>
public sealed record BackupCopy(string Location, long Size, string? Hash, bool Corrupt, bool Identical);

/// <summary>
/// A copy of the files kept somewhere else (the backup server of the "ANalyze parent" integration) that a plan can put back in place
/// of a damaged file (<c>ActionOp.Restore</c>). Implemented by that integration; absent when it is not installed.
/// </summary>
public interface IBackupSource
{
    /// <summary>The copies the backup has of these entries; entries it does not have are left out. Empty when no backup is set up.</summary>
    Task<IReadOnlyDictionary<long, BackupCopy>> FindAsync(IReadOnlyCollection<long> entryIds, CancellationToken ct);

    /// <summary>Every file found damaged here that the backup has: other size or content than the copy, or flagged by the integrity check.</summary>
    Task<IReadOnlyList<long>> DamagedAsync(CancellationToken ct);

    /// <summary>
    /// Streams the backup's copy of an entry (the caller disposes it). Throws <see cref="IOException"/> when the backup cannot be
    /// reached or no longer has the file.
    /// </summary>
    Task<Stream> OpenAsync(long entryId, CancellationToken ct);
}
