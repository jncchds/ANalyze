using System.IO.Hashing;

namespace ANalyze.Scanner;

public static class Hashing
{
    public const int Chunk = 64 * 1024;

    /// <summary>Files up to this size are hashed completely in one step.</summary>
    public const long SmallFile = 3L * Chunk;

    /// <summary>XxHash3 of the first, middle and last 64 KB plus the size: cheap filter before a full hash.</summary>
    public static long QuickHash(string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        var h = new XxHash3();
        var buf = new byte[Chunk];
        foreach (var offset in new[] { 0L, Math.Max(0, size / 2 - Chunk / 2), Math.Max(0, size - Chunk) })
        {
            fs.Position = offset;
            var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            h.Append(buf.AsSpan(0, n));
        }

        h.Append(BitConverter.GetBytes(size));
        return (long)h.GetCurrentHashAsUInt64();
    }

    /// <summary>XxHash128 of the whole content, hex encoded.</summary>
    public static async Task<string> FullHashAsync(string path, Action<long>? onBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var h = new XxHash128();
        var buf = new byte[1 << 20];
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            h.Append(buf.AsSpan(0, n));
            onBytes?.Invoke(n);
        }

        return Convert.ToHexStringLower(h.GetCurrentHash());
    }
}
