using System.Buffers.Binary;
using System.IO.Hashing;
using ANalyze.Scanner;

namespace ANalyze.Integrations.Backup;

/// <summary>
/// A fingerprint of one file's content, read once from start to end, that two copies can be compared with without sending the data:
/// a 64-bit hash per block counted from the start (<see cref="Head"/>) and per block counted from the end (<see cref="Tail"/>, so content
/// that only moved because something was inserted or removed before it still matches), which head blocks are all zeros, exact hashes of
/// the first and last <see cref="Compare"/> bytes, the full XxHash128 (as stored in the index) and the first bytes for the file type.
/// </summary>
public sealed class FileMap
{
    /// <summary>Bytes kept from the start: enough for <see cref="FileTypes.Sniff"/> (ISO images look at 0x8001).</summary>
    public const int StartBytes = 0x8800;

    public long Size { get; init; }
    public long MTime { get; init; }
    /// <summary>XxHash128 of the whole content, hex (same as <c>Entries.FullHash</c>).</summary>
    public required string Hash { get; init; }
    public int Block { get; init; }
    /// <summary>XxHash3 of block i = bytes [i·Block, (i+1)·Block); the last one may be shorter.</summary>
    public required ulong[] Head { get; init; }
    /// <summary>XxHash3 of block j counted from the end = bytes [Size−(j+1)·Block, Size−j·Block), clipped at 0.</summary>
    public required ulong[] Tail { get; init; }
    /// <summary>Head block i holds only zero bytes.</summary>
    public required bool[] Zero { get; init; }
    /// <summary>Length of <see cref="Prefix"/> and <see cref="Suffix"/>: the size of the shorter copy as the asker knew it.</summary>
    public long Compare { get; init; }
    public ulong Prefix { get; init; }
    public ulong Suffix { get; init; }
    public required byte[] Start { get; init; }
    /// <summary>The file changed size or mtime while it was read.</summary>
    public bool Changed { get; init; }

    /// <summary>Block size so a file has at most about 4096 blocks: 64 KiB, or the next power of two above size/4096.</summary>
    public static int ChooseBlock(long size)
    {
        var b = 64L * 1024;
        while (b * 4096 < size && b < (1L << 30))
        {
            b *= 2;
        }

        return (int)b;
    }

    /// <summary>Reads the file once and builds its map. <paramref name="compare"/> is clipped to the size.</summary>
    public static async Task<FileMap> ComputeAsync(string path, int block, long compare, Action<long>? onBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
        var size = fs.Length;
        block = Math.Max(4096, block);
        compare = Math.Clamp(compare, 0, size);
        var count = (int)((size + block - 1) / block);
        var head = new ulong[count];
        var tail = new ulong[count];
        var zero = new bool[count];
        var tailStart = size - (long)(count - 1) * block; // end of the frontmost tail block (it may be short)
        var suffixFrom = size - compare;

        var full = new XxHash128();
        var h = new XxHash3();
        var t = new XxHash3();
        var pre = new XxHash3();
        var suf = new XxHash3();
        var allZero = true;
        var start = Array.Empty<byte>();
        var buf = new byte[1 << 20];
        long pos = 0;
        while (pos < size)
        {
            var want = (int)Math.Min(buf.Length, size - pos);
            var n = await fs.ReadAtLeastAsync(buf.AsMemory(0, want), want, throwOnEndOfStream: false, ct);
            if (n == 0)
            {
                break;
            }

            if (pos == 0)
            {
                start = buf.AsSpan(0, Math.Min(n, StartBytes)).ToArray();
            }

            // Split the chunk at every boundary: head blocks, tail blocks, end of the prefix, start of the suffix.
            var i = 0;
            while (i < n)
            {
                var p = pos + i;
                var next = pos + n;
                next = Math.Min(next, (p / block + 1) * block);
                var tailEnd = p < tailStart ? tailStart : tailStart + ((p - tailStart) / block + 1) * block;
                next = Math.Min(next, tailEnd);
                if (p < compare)
                {
                    next = Math.Min(next, compare);
                }

                if (p < suffixFrom)
                {
                    next = Math.Min(next, suffixFrom);
                }

                var span = buf.AsSpan(i, (int)(next - p));
                full.Append(span);
                h.Append(span);
                t.Append(span);
                allZero &= !span.ContainsAnyExcept((byte)0);
                if (p < compare)
                {
                    pre.Append(span);
                }

                if (p >= suffixFrom)
                {
                    suf.Append(span);
                }

                if (next % block == 0 || next == size)
                {
                    var k = (int)((next - 1) / block);
                    head[k] = h.GetCurrentHashAsUInt64();
                    zero[k] = allZero;
                    h.Reset();
                    allZero = true;
                }

                if (next == tailEnd || next == size)
                {
                    // Tail blocks end at size − j·block.
                    tail[(int)((size - next) / block)] = t.GetCurrentHashAsUInt64();
                    t.Reset();
                }

                i += span.Length;
            }

            pos += n;
            onBytes?.Invoke(n);
        }

        var changed = pos != size || fs.Length != size || new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds() != mtime;
        return new FileMap
        {
            Size = size,
            MTime = mtime,
            Hash = Convert.ToHexStringLower(full.GetCurrentHash()),
            Block = block,
            Head = head,
            Tail = tail,
            Zero = zero,
            Compare = compare,
            Prefix = pre.GetCurrentHashAsUInt64(),
            Suffix = suf.GetCurrentHashAsUInt64(),
            Start = start,
            Changed = changed,
        };
    }

    public FileProbe ToProbe(ProbeIndex? index) => new(Size, MTime, Hash, Block, Pack(Head), Pack(Tail), PackBits(Zero), Compare,
        Prefix.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), Suffix.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToBase64String(Start), Changed, index);

    public static FileMap FromProbe(FileProbe p)
    {
        var head = Unpack(p.Head);
        return new FileMap
        {
            Size = p.Size,
            MTime = p.MTime,
            Hash = p.Hash,
            Block = p.Block,
            Head = head,
            Tail = Unpack(p.Tail),
            Zero = UnpackBits(p.Zero, head.Length),
            Compare = p.Compare,
            Prefix = ulong.Parse(p.Prefix, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
            Suffix = ulong.Parse(p.Suffix, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
            Start = Convert.FromBase64String(p.Start),
            Changed = p.Changed,
        };
    }

    private static string Pack(ulong[] v)
    {
        var b = new byte[v.Length * 8];
        for (var i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i * 8), v[i]);
        }

        return Convert.ToBase64String(b);
    }

    private static ulong[] Unpack(string s)
    {
        var b = Convert.FromBase64String(s);
        var v = new ulong[b.Length / 8];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i * 8));
        }

        return v;
    }

    private static string PackBits(bool[] v)
    {
        var b = new byte[(v.Length + 7) / 8];
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i])
            {
                b[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return Convert.ToBase64String(b);
    }

    private static bool[] UnpackBits(string s, int count)
    {
        var b = Convert.FromBase64String(s);
        var v = new bool[count];
        for (var i = 0; i < count && i / 8 < b.Length; i++)
        {
            v[i] = (b[i / 8] & (1 << (i % 8))) != 0;
        }

        return v;
    }
}

/// <summary>What the parent's index says about the file (it may be older than the content just read).</summary>
public sealed record ProbeIndex(long Size, long MTime, string? Hash, long? HashedAt, long? CorruptAt);

/// <summary><see cref="FileMap"/> on the wire: block hashes and zero flags packed as base64, 64-bit hashes as hex.</summary>
public sealed record FileProbe(long Size, long MTime, string Hash, int Block, string Head, string Tail, string Zero, long Compare,
    string Prefix, string Suffix, string Start, bool Changed, ProbeIndex? Index);

/// <summary>One line of the parent's <c>/backup/probe</c> answer (NDJSON): progress while reading, then the result or an error.</summary>
public sealed record ProbeLine(long? Read = null, bool? Waiting = null, FileProbe? Result = null, string? Error = null);
