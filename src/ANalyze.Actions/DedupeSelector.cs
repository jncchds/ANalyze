using ANalyze.Domain;

namespace ANalyze.Actions;

public sealed record DupMember(long EntryId, int RootId, string RelPath, long MTime, string Identity);

/// <summary>Chooses which copy of a duplicate group to keep.</summary>
public static class DedupeSelector
{
    public static DupMember ChooseKeep(IReadOnlyList<DupMember> members, DedupeKeep strategy, IReadOnlyList<string> preferred)
    {
        if (members.Count == 0)
        {
            throw new ArgumentException("Empty group.", nameof(members));
        }

        IEnumerable<DupMember> ordered = strategy switch
        {
            DedupeKeep.Newest => members.OrderByDescending(m => m.MTime).ThenBy(m => m.RelPath.Length),
            DedupeKeep.ShortestPath => members.OrderBy(m => m.RelPath.Count(c => c == '/')).ThenBy(m => m.RelPath.Length).ThenBy(m => m.MTime),
            DedupeKeep.LongestPath => members.OrderByDescending(m => m.RelPath.Count(c => c == '/')).ThenByDescending(m => m.RelPath.Length).ThenBy(m => m.MTime),
            _ => members.OrderBy(m => m.MTime).ThenBy(m => m.RelPath.Length),
        };

        if (strategy == DedupeKeep.PreferredFolder && preferred.Count > 0)
        {
            // The first preferred folder (in list order) that contains a copy wins; ties go to the oldest copy.
            foreach (var pref in preferred)
            {
                var p = pref.Trim().Trim('/');
                var hit = members.Where(m => p.Length == 0 || m.RelPath == p || m.RelPath.StartsWith(p + "/", StringComparison.Ordinal))
                    .OrderBy(m => m.MTime).ThenBy(m => m.RelPath.Length).FirstOrDefault();
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return ordered.First();
    }
}
