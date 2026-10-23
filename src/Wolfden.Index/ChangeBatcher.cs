using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// What changed in a den during one burst of file events: the paths
/// touched, which of them were created, deleted, or renamed rather than
/// just written, and whether events were lost.
/// </summary>
/// <param name="Paths">Every den-relative path (with <c>/</c>) an event named, old and new names alike.</param>
/// <param name="Moved">The paths among <see cref="Paths"/> that were created, deleted, or renamed, so a folder among them must be looked into.</param>
/// <param name="Overflowed">True if the watcher lost events, so only a full rebuild is safe.</param>
internal sealed record ChangeBatch(IReadOnlySet<string> Paths, IReadOnlySet<string> Moved, bool Overflowed);

/// <summary>
/// Collects a den store's changes (<see cref="DenChange"/>) into batches (#14). Editors save in bursts (a
/// temporary file, a rename, a delete), and a git checkout touches many
/// files at once, so events are coalesced by path, and a batch is ready
/// once events have stopped for <see cref="Quiet"/>, or <see cref="MaxDelay"/>
/// after its first event if they keep coming. It ignores hidden paths
/// (F-LAY-2), which covers <c>.wolfden/</c>, where the index itself is
/// written, and <c>.git/</c>. It keeps no clock of its own, so tests can
/// drive it with any times.
/// </summary>
internal sealed class ChangeBatcher
{
    private HashSet<string> _paths = new(StringComparer.Ordinal);
    private HashSet<string> _moved = new(StringComparer.Ordinal);
    private bool _overflowed;
    private DateTime? _first;
    private DateTime _last;

    /// <param name="quiet">How long events must stop before a batch is ready.</param>
    /// <param name="maxDelay">The longest a batch waits after its first event.</param>
    public ChangeBatcher(TimeSpan quiet, TimeSpan maxDelay)
    {
        Quiet = quiet;
        MaxDelay = maxDelay;
    }

    /// <summary>How long events must stop before a batch is ready.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>The longest a batch waits after its first event.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>True if any event is waiting.</summary>
    public bool HasPending => _first is not null;

    /// <summary>Adds den-relative paths to look at again, such as files that couldn't be read because an editor was still writing them.</summary>
    public void AddAgain(IEnumerable<string> paths, DateTime now)
    {
        var any = false;
        foreach (var path in paths)
        {
            any |= _paths.Add(path);
        }

        if (any)
        {
            Touch(now);
        }
    }

    /// <summary>The watcher lost events (its buffer overflowed), so the next batch must rebuild the index.</summary>
    public void AddOverflow(DateTime now)
    {
        _overflowed = true;
        Touch(now);
    }

    /// <summary>How long until the waiting events make a batch, zero if they already do, or null if nothing is waiting.</summary>
    public TimeSpan? DueIn(DateTime now)
    {
        if (_first is not { } first)
        {
            return null;
        }

        var due = Min(_last + Quiet, first + MaxDelay);
        return due <= now ? TimeSpan.Zero : due - now;
    }

    /// <summary>The batch, if it's ready at <paramref name="now"/>, and starts a new one; otherwise null.</summary>
    public ChangeBatch? Take(DateTime now)
    {
        if (DueIn(now) != TimeSpan.Zero)
        {
            return null;
        }

        var batch = new ChangeBatch(_paths, _moved, _overflowed);
        _paths = new HashSet<string>(StringComparer.Ordinal);
        _moved = new HashSet<string>(StringComparer.Ordinal);
        _overflowed = false;
        _first = null;
        return batch;
    }

    /// <summary>
    /// Adds a change a den store's feed reported (<see cref="IDenChangeFeed"/>),
    /// with den-relative paths. A created, deleted, or renamed path is a
    /// move, so a folder among them is looked into. Hidden paths are
    /// dropped (F-LAY-2): a rename out of a hidden folder keeps only the
    /// visible name.
    /// </summary>
    public void Add(DenChange change, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(change);
        var moved = change.Kind != DenChangeKind.Changed;
        var any = AddDenPath(change.Path, moved);
        if (change.OldPath is not null)
        {
            any |= AddDenPath(change.OldPath, moved);
        }

        if (any)
        {
            Touch(now);
        }
    }

    /// <summary>True unless a name on <paramref name="relative"/> is empty or hidden (F-LAY-2).</summary>
    internal static bool IsDenPath(string relative) =>
        !relative.Split('/').Any(n => n.Length == 0 || n.StartsWith('.'));

    private bool AddDenPath(string relative, bool moved)
    {
        if (!IsDenPath(relative))
        {
            return false;
        }

        _paths.Add(relative);
        if (moved)
        {
            _moved.Add(relative);
        }

        return true;
    }

    private void Touch(DateTime now)
    {
        _first ??= now;
        _last = now;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
