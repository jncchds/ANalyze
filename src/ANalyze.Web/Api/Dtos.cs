using ANalyze.Domain;

namespace ANalyze.Web.Api;

public sealed record EntryDto(
    long Id,
    int RootId,
    long? ParentId,
    string Name,
    string RelPath,
    bool IsDir,
    bool IsSymlink,
    bool BrokenLink,
    long Size,
    long Alloc,
    long Files,
    long Dirs,
    long MTime,
    long ATime,
    FileKind Kind,
    string? Mime,
    string? Ext,
    int? CategoryId,
    CategorySource CategorySource,
    string[] Tags,
    string? Hash,
    int NLink,
    bool Error)
{
    public static EntryDto From(Entry e) => new(
        e.Id, e.RootId, e.ParentId, e.Name, e.RelPath, e.IsDir, e.IsSymlink, e.BrokenLink,
        e.IsDir ? e.TotalSize : e.Size, e.IsDir ? e.TotalAlloc : e.AllocSize, e.FileCount, e.DirCount,
        e.MTime, e.ATime, e.Kind, e.Mime, e.Ext, e.CategoryId, e.CategorySource, SplitTags(e.Tags), e.FullHash, e.NLink, e.Error);

    public static string[] SplitTags(string? tags) =>
        string.IsNullOrEmpty(tags) ? [] : tags.Split('|', StringSplitOptions.RemoveEmptyEntries);
}

public sealed record Page<T>(IReadOnlyList<T> Items, long Total, long? TotalSize = null);

public sealed record IdsRequest(long[] Ids);
