using ANalyze.Scanner;

namespace ANalyze.Integrations.Backup;

/// <summary>Verdicts of <see cref="BackupDiff.Analyze"/>.</summary>
public static class DiffVerdicts
{
    /// <summary>Same content now: the difference came from an outdated hash or size in an index.</summary>
    public const string Identical = "identical";
    /// <summary>The shorter copy is the start of the longer one.</summary>
    public const string Truncated = "truncated";
    /// <summary>The shorter copy is the start of the longer one, and the longer one is newer: data was appended after the backup.</summary>
    public const string Appended = "appended";
    /// <summary>Where the copies differ, one of them holds only zeros.</summary>
    public const string Zeros = "zeros";
    /// <summary>Same size and mtime, a few blocks differ: silent corruption.</summary>
    public const string BitRot = "bitrot";
    /// <summary>Only the beginning differs (headers, tags).</summary>
    public const string HeaderChanged = "header";
    /// <summary>Only the end differs (trailing metadata or index).</summary>
    public const string TrailerChanged = "trailer";
    /// <summary>Start and end match, something in between was inserted, removed or rewritten.</summary>
    public const string MiddleChanged = "middle";
    /// <summary>Same size, scattered blocks differ.</summary>
    public const string Edited = "edited";
    /// <summary>Little or nothing in common: replaced, re-encoded or re-downloaded.</summary>
    public const string Different = "different";
    /// <summary>A copy changed while it was read: compare again later.</summary>
    public const string Changing = "changing";
}

/// <summary>One side of a comparison as shown to the user.</summary>
/// <param name="IndexHash">Content hash stored in that instance's index (may be older than <see cref="Hash"/>).</param>
/// <param name="CorruptAt">That instance found the content changed without a new size or mtime when re-reading it (unix seconds).</param>
/// <param name="Mime">Type detected from the first bytes; null when nothing matched.</param>
/// <param name="StartHex">The first 16 bytes, hex.</param>
public sealed record DiffSide(long Size, long MTime, string Hash, string? IndexHash, long? HashedAt, long? CorruptAt, string? Mime, string StartHex, long ZeroBytes);

/// <summary>A stretch where the copies differ. Offsets are on this instance's copy unless the sizes differ (then both are given).</summary>
public sealed record DiffRange(long Offset, long Length, long ParentOffset, long ParentLength, bool ZeroHere, bool ZeroParent);

/// <param name="Verdict">One of <see cref="DiffVerdicts"/>.</param>
/// <param name="Damaged">"here", "parent" or null: the copy that is most likely the broken or outdated one.</param>
/// <param name="Newer">"here", "parent" or null (same mtime): the copy modified last.</param>
/// <param name="CommonPrefix">Bytes equal from the start (exact when <see cref="Exact"/>, else block-granular).</param>
/// <param name="CommonSuffix">Bytes equal at the end.</param>
/// <param name="DifferentBytes">Bytes that differ on this instance's copy (sizes equal), or the differing middle of the longer copy.</param>
public sealed record DiffReport(
    string Verdict,
    string Summary,
    string Advice,
    IReadOnlyList<string> Details,
    string? Damaged,
    string? Newer,
    DiffSide Here,
    DiffSide Parent,
    int Block,
    int Blocks,
    int DifferentBlocks,
    long DifferentBytes,
    long CommonPrefix,
    long CommonSuffix,
    bool Exact,
    IReadOnlyList<DiffRange> Ranges);

/// <summary>What the index of each side knows about the file, next to the content just read.</summary>
public sealed record DiffIndex(string? Hash, long? HashedAt, long? CorruptAt);

/// <summary>
/// Explains how a file here differs from its copy on the parent, from the two <see cref="FileMap"/>s: equal start and end, which
/// blocks differ, zeros on one side, sizes, modification times and what each index noticed (hashes, silent changes).
/// </summary>
public static class BackupDiff
{
    public const string Here = "here";
    public const string Parent = "parent";

    /// <summary>Modification times this close count as equal (FAT and some copy tools round to 2 s).</summary>
    private const long SameTime = 2;

    /// <summary>Ranges listed in a report; the counts cover all of them.</summary>
    private const int MaxRanges = 50;

    public static DiffReport Analyze(FileMap here, FileMap parent, DiffIndex hereIndex, DiffIndex parentIndex, string parentName)
    {
        var b = here.Block;
        var hereSide = Side(here, hereIndex);
        var parentSide = Side(parent, parentIndex);
        var newer = Math.Abs(here.MTime - parent.MTime) <= SameTime ? null : here.MTime > parent.MTime ? Here : Parent;
        var details = new List<string>();
        var p = parentName;

        details.Add(newer is null
            ? "Both copies have the same modification time."
            : $"{(newer == Here ? "This copy" : $"The copy on {p}")} was modified {Span(Math.Abs(here.MTime - parent.MTime))} later.");

        if (here.Size != parent.Size)
        {
            details.Add($"Sizes: {Bytes(here.Size)} here, {Bytes(parent.Size)} on {p} ({(here.Size > parent.Size ? "−" : "+")}{Bytes(Math.Abs(here.Size - parent.Size))} on {p}).");
        }

        if (hereSide.Mime != parentSide.Mime)
        {
            details.Add($"Type from the first bytes: {hereSide.Mime ?? "unknown"} here, {parentSide.Mime ?? "unknown"} on {p}.");
        }

        if (hereIndex.CorruptAt is { } hc)
        {
            details.Add($"This instance noticed on {Date(hc)} that the file's content changed while its size and modification time stayed the same.");
        }

        if (parentIndex.CorruptAt is { } pc)
        {
            details.Add($"{p} noticed on {Date(pc)} that its copy's content changed while its size and modification time stayed the same.");
        }

        if (here.Changed || parent.Changed)
        {
            return Report(DiffVerdicts.Changing, $"{(here.Changed ? "This copy" : $"The copy on {p}")} changed while it was read.",
                "Compare again when nothing writes to the file.", null);
        }

        if (here.Hash == parent.Hash)
        {
            if (hereIndex.Hash is { } hi && hi != here.Hash)
            {
                details.Add("This instance's stored hash is outdated: the file changed after it was hashed.");
            }

            if (parentIndex.Hash is { } pi && pi != parent.Hash)
            {
                details.Add($"{p}'s stored hash is outdated: its copy changed after it was hashed.");
            }

            return Report(DiffVerdicts.Identical, "Both copies have the same content now; the difference came from outdated index data.",
                "Scan the folder here and on the parent (or wait for the next scans), then compare the backup again.", null);
        }

        // Equal bytes from the start: whole equal head blocks, or exactly the shorter copy's length when the prefix hashes agree.
        var min = Math.Min(here.Size, parent.Size);
        var exact = here.Compare == min && parent.Compare == min;
        var prefix = CommonBlocks(here.Head, parent.Head, min, b);
        var suffix = CommonBlocks(here.Tail, parent.Tail, min, b);
        if (exact && here.Prefix == parent.Prefix)
        {
            prefix = min;
        }

        if (exact && here.Suffix == parent.Suffix)
        {
            suffix = min;
        }

        // Same size: block-by-block differences.
        var ranges = new List<DiffRange>();
        var blocks = Math.Max(here.Head.Length, parent.Head.Length);
        int diffBlocks;
        long diffBytes, zeroHere = 0, zeroParent = 0;
        if (here.Size == parent.Size)
        {
            diffBlocks = 0;
            diffBytes = 0;
            for (var i = 0; i < here.Head.Length; i++)
            {
                if (here.Head[i] == parent.Head[i])
                {
                    continue;
                }

                var off = (long)i * b;
                var len = Math.Min(b, here.Size - off);
                diffBlocks++;
                diffBytes += len;
                if (here.Zero[i] && !parent.Zero[i])
                {
                    zeroHere += len;
                }
                else if (parent.Zero[i] && !here.Zero[i])
                {
                    zeroParent += len;
                }

                if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == off && ranges[^1].ZeroHere == here.Zero[i] && ranges[^1].ZeroParent == parent.Zero[i])
                {
                    var last = ranges[^1];
                    ranges[^1] = last with { Length = last.Length + len, ParentLength = last.ParentLength + len };
                }
                else
                {
                    ranges.Add(new DiffRange(off, len, off, len, here.Zero[i], parent.Zero[i]));
                }
            }
        }
        else
        {
            // Different sizes: the part between the common start and the common end, on each copy.
            var overlap = Math.Max(0, prefix + suffix - min);
            var s = suffix - overlap;
            var hereLen = here.Size - prefix - s;
            var parentLen = parent.Size - prefix - s;
            diffBytes = Math.Max(hereLen, parentLen);
            diffBlocks = (int)((diffBytes + b - 1) / b);
            zeroHere = ZeroBytes(here, prefix, hereLen);
            zeroParent = ZeroBytes(parent, prefix, parentLen);
            ranges.Add(new DiffRange(prefix, hereLen, prefix, parentLen, hereLen > 0 && zeroHere >= hereLen, parentLen > 0 && zeroParent >= parentLen));
        }

        var report = (string verdict, string summary, string advice, string? damaged) =>
        {
            details.Add(DescribeCommon(prefix, suffix, here.Size, parent.Size, exact));
            return new DiffReport(verdict, summary, advice, details, damaged, newer, hereSide, parentSide, b, blocks, diffBlocks, diffBytes,
                prefix, suffix, exact, ranges.Count > MaxRanges ? ranges[..MaxRanges] : ranges);
        };

        // The shorter copy is exactly the beginning of the longer one.
        if (here.Size != parent.Size && exact && prefix == min)
        {
            var shorter = here.Size < parent.Size ? Here : Parent;
            var longer = shorter == Here ? Parent : Here;
            var missing = Bytes(Math.Abs(here.Size - parent.Size));
            var shortName = shorter == Here ? "This copy" : $"The copy on {p}";
            if (newer == longer)
            {
                return report(DiffVerdicts.Appended,
                    $"{(longer == Here ? "The file grew here" : $"The file grew on {p}")} after the other copy was made: {missing} were added at the end.",
                    longer == Here
                        ? $"The backup is outdated, not broken: let the next backup run copy the file to {p} again."
                        : $"{p} has a newer, longer version; copy it back here if that is the one you want.",
                    shorter);
            }

            return report(DiffVerdicts.Truncated,
                $"{shortName} is cut off: it ends {missing} early, everything before that is identical.",
                shorter == Parent ? $"An interrupted copy: copy the file to {p} again." : $"This copy is incomplete: restore it from {p}.",
                shorter);
        }

        // Where they differ, one side is only zeros: an interrupted, preallocated or sparse copy, or a disk that returned zeros.
        var zeros = Math.Max(zeroHere, zeroParent);
        if (diffBytes > 0 && zeros >= diffBytes * 0.8)
        {
            var side = zeroParent >= zeroHere ? Parent : Here;
            return report(DiffVerdicts.Zeros,
                $"{(side == Here ? "This copy" : $"The copy on {p}")} has {Bytes(zeros)} of zeros where the other one has data.",
                side == Parent
                    ? $"Typical of a copy that stopped while writing (the space was reserved but never filled): copy the file to {p} again."
                    : $"This copy lost data (an interrupted write or a disk error): restore it from {p}.",
                side);
        }

        var share = blocks == 0 ? 0 : (double)diffBlocks / blocks;
        var damagedByIndex = hereIndex.CorruptAt is not null && parentIndex.CorruptAt is null ? Here
            : parentIndex.CorruptAt is not null && hereIndex.CorruptAt is null ? Parent : null;

        if (here.Size == parent.Size)
        {
            if (share >= 0.9 && blocks > 2)
            {
                return Different();
            }

            if (newer is null && (diffBlocks <= 16 || share <= 0.05))
            {
                var where = damagedByIndex switch
                {
                    Here => "This instance saw its copy change without a new modification time, so the damage is here.",
                    Parent => $"{p} saw its copy change without a new modification time, so the damage is on {p}.",
                    _ => "Which copy is damaged cannot be told from the content alone; open both if the type allows (a damaged video or image shows artefacts).",
                };
                return report(DiffVerdicts.BitRot,
                    $"{Count(diffBlocks, "block")} ({Bytes(diffBytes)}) differ although size and modification time are the same: silent corruption (bit rot, a failing disk, RAM or controller).",
                    where + (damagedByIndex == Here ? $" Restore the file from {p}." : damagedByIndex == Parent ? $" Copy the file to {p} again and check that disk." : ""),
                    damagedByIndex);
            }

            if (ranges.Count > 0 && ranges[0].Offset == 0 && ranges[^1].Offset + ranges[^1].Length <= Math.Max(2L * b, here.Size / 10))
            {
                return report(DiffVerdicts.HeaderChanged,
                    $"Only the beginning differs (the first {Bytes(ranges[^1].Offset + ranges[^1].Length)}): metadata such as tags or a header was rewritten in place.",
                    Edit(), null);
            }

            if (ranges.Count > 0 && ranges[0].Offset >= here.Size - Math.Max(2L * b, here.Size / 10))
            {
                return report(DiffVerdicts.TrailerChanged,
                    $"Only the end differs (the last {Bytes(here.Size - ranges[0].Offset)}): trailing metadata or an index was rewritten.",
                    Edit(), null);
            }

            return report(DiffVerdicts.Edited,
                $"{Count(diffBlocks, "block")} of {blocks:N0} differ in {Count(ranges.Count, "place")} ({Bytes(diffBytes)}); the rest is identical: the file was edited in place.",
                Edit(), newer is null ? damagedByIndex : Older());
        }

        // Different sizes, not a plain cut-off.
        if (prefix + suffix >= min / 2 && min > 0)
        {
            if (prefix < Math.Max(2L * b, min / 10))
            {
                return report(DiffVerdicts.HeaderChanged,
                    $"Only the beginning differs ({Bytes(ranges[0].Length)} here, {Bytes(ranges[0].ParentLength)} on {p}); the remaining {Bytes(suffix)} are identical: a header or tags grew or shrank (common when a tagger rewrites audio or video metadata).",
                    Edit(), null);
            }

            if (suffix < Math.Max(2L * b, min / 10))
            {
                return report(DiffVerdicts.TrailerChanged,
                    $"The first {Bytes(prefix)} are identical, only the end differs ({Bytes(ranges[0].Length)} here, {Bytes(ranges[0].ParentLength)} on {p}): trailing metadata or an index was rewritten, or a write stopped part-way.",
                    Edit(), Older());
            }

            return report(DiffVerdicts.MiddleChanged,
                $"Start ({Bytes(prefix)}) and end ({Bytes(suffix)}) are identical; {Bytes(ranges[0].Length)} here and {Bytes(ranges[0].ParentLength)} on {p} in between differ: content was inserted, removed or rewritten in the middle.",
                Edit(), Older());
        }

        return Different();

        DiffReport Different() => report(DiffVerdicts.Different,
            $"The copies have {(prefix + suffix == 0 ? "nothing" : "little")} in common: the file was replaced, re-encoded or downloaded again{(newer is null ? "" : $"; {(newer == Here ? "this copy" : $"the one on {p}")} is newer")}.",
            newer == Here ? $"If this copy is the right one, let the next backup copy it to {p}." : newer == Parent ? $"{p} has the newer version; copy it back if that is the one you want." : "Open both to decide which one to keep.",
            Older());

        string Edit() => newer switch
        {
            Here => $"This copy is newer: the backup is outdated, let the next backup run copy it to {p}.",
            Parent => $"The copy on {p} is newer; copy it back if that is the one you want.",
            _ => "Both have the same modification time, so the editing program restored it; decide which version to keep.",
        };

        string? Older() => newer switch { Here => Parent, Parent => Here, _ => damagedByIndex };

        DiffReport Report(string verdict, string summary, string advice, string? damaged) =>
            new(verdict, summary, advice, details, damaged, newer, hereSide, parentSide, b, Math.Max(here.Head.Length, parent.Head.Length), 0, 0, 0, 0, false, []);
    }

    /// <summary>Bytes in whole equal blocks from the start (or end), stopping at the first difference; never more than <paramref name="min"/>.</summary>
    private static long CommonBlocks(ulong[] a, ulong[] c, long min, int block)
    {
        // A block only counts when it is full on both sides: the frontmost tail / last head block may be shorter.
        var full = (int)(min / block);
        var n = 0;
        while (n < full && n < a.Length && n < c.Length && a[n] == c[n])
        {
            n++;
        }

        return Math.Min(min, (long)n * block);
    }

    /// <summary>Zero bytes of a copy in [offset, offset+length), counted by whole head blocks inside the range.</summary>
    private static long ZeroBytes(FileMap m, long offset, long length)
    {
        long z = 0;
        if (length <= 0)
        {
            return 0;
        }

        var first = (int)(offset / m.Block);
        var last = (int)((offset + length - 1) / m.Block);
        for (var i = first; i <= last && i < m.Zero.Length; i++)
        {
            if (m.Zero[i])
            {
                var from = Math.Max(offset, (long)i * m.Block);
                var to = Math.Min(offset + length, Math.Min(m.Size, (long)(i + 1) * m.Block));
                z += Math.Max(0, to - from);
            }
        }

        return z;
    }

    private static DiffSide Side(FileMap m, DiffIndex index)
    {
        var zeros = 0L;
        for (var i = 0; i < m.Zero.Length; i++)
        {
            if (m.Zero[i])
            {
                zeros += Math.Min(m.Block, m.Size - (long)i * m.Block);
            }
        }

        return new DiffSide(m.Size, m.MTime, m.Hash, index.Hash, index.HashedAt, index.CorruptAt,
            m.Size == 0 ? null : m.Start.AsSpan().ContainsAnyExcept((byte)0) ? FileTypes.Sniff(m.Start)?.Mime : "zeros",
            Convert.ToHexStringLower(m.Start.AsSpan(0, Math.Min(16, m.Start.Length))), zeros);
    }

    private static string DescribeCommon(long prefix, long suffix, long hereSize, long parentSize, bool exact) =>
        $"Identical from the start: {Bytes(prefix)}; identical at the end: {Bytes(suffix)}{(exact ? "" : " (to the block)")}, of {Bytes(Math.Max(hereSize, parentSize))}.";

    private static string Count(long n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

    private static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    internal static string Span(long seconds) => seconds switch
    {
        < 120 => $"{seconds} s",
        < 7200 => $"{seconds / 60} min",
        < 172800 => $"{seconds / 3600} h",
        _ => $"{seconds / 86400} days",
    };

    internal static string Bytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = n;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{n:N0} B" : v.ToString(v < 10 ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[u];
    }
}
