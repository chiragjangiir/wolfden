namespace Wolfden.Index;

/// <summary>
/// How the rows that come from single files got their ids. A full build
/// gives ids out in order, 1, 2, 3, as it always has. An incremental update
/// (#14) gives a row the id its natural key had before, such as an entity's
/// path or a section's entity and place, and a new id, above every id used
/// so far, to a key that's new. So a row that didn't change keeps its id and
/// isn't rewritten, and one file's change rewrites only that file's rows.
/// </summary>
/// <remarks>
/// Ids are handles within one index file, not data: after updates they no
/// longer follow path order, and readers order by the columns instead.
/// </remarks>
internal sealed class IndexIds
{
    private IndexIds(IndexIds? previous)
    {
        Files = new IdTable(previous?.Files);
        Entities = new IdTable(previous?.Entities);
        Aliases = new IdTable(previous?.Aliases);
        FieldValues = new IdTable(previous?.FieldValues);
        Sections = new IdTable(previous?.Sections);
        Search = new IdTable(previous?.Search);
        Links = new IdTable(previous?.Links);
        OutlineRows = new IdTable(previous?.OutlineRows);
    }

    /// <summary>Files, by path.</summary>
    public IdTable Files { get; }

    /// <summary>Entities, by path.</summary>
    public IdTable Entities { get; }

    /// <summary>Aliases, by entity path and place in <c>aliases</c>.</summary>
    public IdTable Aliases { get; }

    /// <summary>Field values, by entity path and place among the entity's values.</summary>
    public IdTable FieldValues { get; }

    /// <summary>Sections, by entity path and section index.</summary>
    public IdTable Sections { get; }

    /// <summary>Search rows, by entity path and section index (-1 for the entity's own row).</summary>
    public IdTable Search { get; }

    /// <summary>Links, by path and place among the file's links.</summary>
    public IdTable Links { get; }

    /// <summary>Outline rows, by path and place among the outline's rows.</summary>
    public IdTable OutlineRows { get; }

    /// <summary>Ids for a full build: 1, 2, 3, in the order they're taken.</summary>
    public static IndexIds Fresh() => new(null);

    /// <summary>Ids for an update: each key's id from <paramref name="previous"/>, or a new one.</summary>
    public static IndexIds After(IndexIds previous) => new(previous);
}

/// <summary>The ids of one table, by owner (a path) and a key within the owner.</summary>
internal sealed class IdTable
{
    private readonly IdTable? _previous;
    private readonly Dictionary<string, Dictionary<int, int>> _owners = new(StringComparer.Ordinal);

    public IdTable(IdTable? previous)
    {
        _previous = previous;
        Next = previous?.Next ?? 1;
    }

    /// <summary>The id the next new key gets: above every id given out so far, here or before.</summary>
    public int Next { get; private set; }

    /// <summary>The id for <paramref name="key"/> of <paramref name="owner"/>: the one it had before, or a new one.</summary>
    public int Take(string owner, int key)
    {
        if (!_owners.TryGetValue(owner, out var keys))
        {
            _owners[owner] = keys = [];
        }

        if (_previous is null || !_previous._owners.TryGetValue(owner, out var before) || !before.TryGetValue(key, out var id))
        {
            id = Next++;
        }

        keys.Add(key, id);
        return id;
    }

    /// <summary>Keeps every id <paramref name="owner"/> had, for rows that are kept as they were.</summary>
    public void Keep(string owner)
    {
        if (_previous is not null && _previous._owners.TryGetValue(owner, out var before))
        {
            // Never changed after it's built, so it can be shared.
            _owners[owner] = before;
        }
    }
}
