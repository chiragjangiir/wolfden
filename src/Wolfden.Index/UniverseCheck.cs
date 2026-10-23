using Microsoft.Data.Sqlite;
using Wolfden.Core.Checks;

namespace Wolfden.Index;

/// <summary>
/// A check of one universe (maintainer decision, 2026-10-23) with the ids the
/// other universes use taken from the index, so an id shared across
/// universes is found (F-ID-3) without reading the other universes' notes.
/// </summary>
/// <param name="Check">The check.</param>
/// <param name="SkippedNote">
/// Null if ids were compared across universes; otherwise why that part was
/// skipped, such as an index that couldn't be built. The rest of the check
/// ran either way.
/// </param>
public sealed record UniverseCheck(DenCheck Check, string? SkippedNote)
{
    /// <summary>
    /// Checks universe <paramref name="universeKey"/> of the den at
    /// <paramref name="root"/>. It brings the index up to date first (hashing
    /// the den's files, and waiting up to <paramref name="watcherWait"/> for a
    /// running watcher), which costs more than the check of one universe
    /// alone, but a stale index could miss a duplicate.
    /// </summary>
    public static UniverseCheck Run(string root, string universeKey, TimeSpan watcherWait)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(universeKey);
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ids = null;
        string? skipped = null;
        try
        {
            IndexBuilder.EnsureUpToDate(root, watcherWait);
            using var index = DenIndex.Open(root);
            if (index.Context.Dens.Single().IsLoaded)
            {
                ids = index.IdsOutside(universeKey);
            }
            else
            {
                skipped = "den.yaml doesn't load, so the index has no notes to compare ids with.";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            skipped = $"the index couldn't be read or rebuilt ({e.Message}).";
        }

        var check = DenCheck.Run(root, universeKey, ids);
        return new UniverseCheck(
            check,
            skipped is null ? null : $"Ids shared with notes in other universes (F-ID-3) weren't checked: {skipped} Check the whole den to find them.");
    }
}
