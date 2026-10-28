using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANalyze.Infrastructure.Roots;

/// <summary>
/// Keeps the Roots table in sync with configuration, probes whether each root is writable,
/// and resolves entry paths safely (nothing outside a root, no symlink escapes).
/// </summary>
public sealed class RootRegistry(IServiceScopeFactory scopes, IOptions<AnalyzeOptions> options, ILogger<RootRegistry> log)
{
    public const string TrashFolder = ".analyze-trash";

    private volatile IReadOnlyList<Root> _roots = [];

    public IReadOnlyList<Root> All => _roots;

    public Root? Get(int id) => _roots.FirstOrDefault(r => r.Id == id);

    public Root Require(int id) => Get(id) ?? throw new KeyNotFoundException($"Root {id} not found.");

    public async Task SyncAsync(CancellationToken ct = default)
    {
        var configured = options.Value.Roots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct()
            .ToList();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyzeDbContext>();
        var rows = await db.Roots.ToListAsync(ct);
        foreach (var path in configured)
        {
            var row = rows.FirstOrDefault(r => r.Path == path);
            if (row is null)
            {
                row = new Root { Path = path, Name = NameOf(path) };
                db.Roots.Add(row);
                rows.Add(row);
            }

            row.Missing = !Directory.Exists(path);
            row.ReadOnly = options.Value.ReadOnly || row.Missing || !ProbeWritable(path);
            log.LogInformation("Root {Path}: {State}", path, row.Missing ? "missing" : row.ReadOnly ? "read-only" : "read-write");
        }

        foreach (var row in rows.Where(r => !configured.Contains(r.Path)))
        {
            // Data of roots removed from configuration is kept but hidden.
            row.Missing = true;
            row.ReadOnly = true;
        }

        await db.SaveChangesAsync(ct);
        _roots = rows.Where(r => configured.Contains(r.Path)).OrderBy(r => r.Path).ToList();
    }

    /// <summary>Re-checks writability right before actions run (a mount can be remounted read-only).</summary>
    public bool IsWritable(int rootId)
    {
        var root = Get(rootId);
        if (root is null || options.Value.ReadOnly || !Directory.Exists(root.Path))
        {
            return false;
        }

        var ok = ProbeWritable(root.Path);
        root.ReadOnly = !ok;
        return ok;
    }

    public void UpdateCached(Root updated)
    {
        var r = Get(updated.Id);
        if (r is null)
        {
            return;
        }

        r.RootEntryId = updated.RootEntryId;
        r.LastScanAt = updated.LastScanAt;
    }

    /// <summary>Full path of an entry. Throws when the relative path escapes the root.</summary>
    public string FullPath(int rootId, string relPath) => Combine(Require(rootId).Path, relPath);

    public static string Combine(string rootPath, string relPath)
    {
        if (relPath.Length == 0)
        {
            return rootPath;
        }

        var full = Path.GetFullPath(Path.Combine(rootPath, relPath));
        if (!IsUnder(rootPath, full))
        {
            throw new UnauthorizedAccessException("Path escapes the root.");
        }

        return full;
    }

    /// <summary>
    /// Full path for reading file content: also resolves symlinks in every component and refuses targets
    /// outside the root, so a link planted after the scan cannot expose other files.
    /// </summary>
    public string ResolveForRead(int rootId, string relPath)
    {
        var root = Require(rootId);
        var full = Combine(root.Path, relPath);
        var real = Native.RealPath(full) ?? throw new FileNotFoundException("File not found.", relPath);
        var realRoot = Native.RealPath(root.Path) ?? root.Path;
        if (!IsUnder(realRoot, real))
        {
            throw new UnauthorizedAccessException("Path resolves outside the root.");
        }

        return real;
    }

    public static bool IsUnder(string rootPath, string fullPath) =>
        fullPath == rootPath || fullPath.StartsWith(rootPath.EndsWith('/') ? rootPath : rootPath + "/", StringComparison.Ordinal);

    public static string TrashPath(string rootPath, int planId, string relPath) =>
        Path.Combine(rootPath, TrashFolder, planId.ToString(System.Globalization.CultureInfo.InvariantCulture), relPath);

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool ProbeWritable(string path)
    {
        var probe = Path.Combine(path, $".analyze-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
