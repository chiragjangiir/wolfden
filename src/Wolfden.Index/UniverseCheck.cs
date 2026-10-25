using Microsoft.Data.Sqlite;
using Wolfden.Core.Checks;
using Wolfden.Core.Structure;

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
    /// <remarks>
    /// It reads the den's structure first, and leaves the index alone when
    /// <c>den.yaml</c> doesn't load (the note says so) or the den has no
    /// universe <paramref name="universeKey"/>. The caller reports an
    /// unknown universe from <see cref="DenCheck.Structure"/> without having
    /// waited for the whole den to be hashed (#130).
    /// </remarks>
    public static UniverseCheck Run(string root, string universeKey, TimeSpan watcherWait) =>
        Run(root, universeKey, watcherWait, useIndex: true);

    /// <summary>
    /// <see cref="Run(string, string, TimeSpan)"/>, or with
    /// <paramref name="useIndex"/> false, without the index at all, so
    /// nothing is written under <c>.wolfden/</c>: ids aren't compared with
    /// the other universes' notes, and the note says so.
    /// </summary>
    public static UniverseCheck Run(string root, string universeKey, TimeSpan watcherWait, bool useIndex)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(universeKey);
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ids = null;
        string? skipped = null;

        // Only den.yaml, packs, and container files: far less than hashing the den.
        var structure = DenStructure.Load(root);
        if (!structure.IsLoaded)
        {
            skipped = NotLoaded;
        }
        else if (structure.FindUniverse(universeKey) is null)
        {
            // The caller reports the unknown universe. Checking the den-level files alone is cheap.
            return new UniverseCheck(DenCheck.Run(root, universeKey), null);
        }
        else if (!useIndex)
        {
            skipped = "the check was asked not to use the index.";
        }
        else
        {
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
                    // den.yaml changed after it was read above.
                    skipped = NotLoaded;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
            {
                skipped = $"the index couldn't be read or rebuilt ({e.Message}).";
            }
        }

        var check = DenCheck.Run(root, universeKey, ids);
        return new UniverseCheck(
            check,
            skipped is null ? null : $"Ids shared with notes in other universes (F-ID-3) weren't checked: {skipped} Check the whole den to find them.");
    }

    private const string NotLoaded = "den.yaml doesn't load, so the index has no notes to compare ids with.";
}
