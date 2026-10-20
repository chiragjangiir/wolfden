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
/// Collects file events into batches (#14). Editors save in bursts (a
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
    private readonly string _root;
    private HashSet<string> _paths = new(StringComparer.Ordinal);
    private HashSet<string> _moved = new(StringComparer.Ordinal);
    private bool _overflowed;
    private DateTime? _first;
    private DateTime _last;

    /// <param name="root">The den root's full path.</param>
    /// <param name="quiet">How long events must stop before a batch is ready.</param>
    /// <param name="maxDelay">The longest a batch waits after its first event.</param>
    public ChangeBatcher(string root, TimeSpan quiet, TimeSpan maxDelay)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Quiet = quiet;
        MaxDelay = maxDelay;
    }

    /// <summary>How long events must stop before a batch is ready.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>The longest a batch waits after its first event.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>True if any event is waiting.</summary>
    public bool HasPending => _first is not null;

    /// <summary>
    /// Adds a file event: <paramref name="fullPath"/> was created, changed,
    /// deleted, or (with <paramref name="oldFullPath"/>) renamed. Events for
    /// hidden paths or paths outside the den are dropped.
    /// </summary>
    public void Add(WatcherChangeTypes kind, string fullPath, string? oldFullPath, DateTime now)
    {
        var moved = kind != WatcherChangeTypes.Changed;
        var any = AddPath(fullPath, moved);
        if (oldFullPath is not null)
        {
            any |= AddPath(oldFullPath, moved);
        }

        if (any)
        {
            Touch(now);
        }
    }

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
    /// <paramref name="fullPath"/> relative to the den root, with <c>/</c>,
    /// or null if it's the root itself, outside it, or hidden: a name on the
    /// way starts with <c>.</c> (F-LAY-2).
    /// </summary>
    public static string? Relative(string root, string fullPath)
    {
        // Only the OS's separators: on Linux a '\' is part of a file name.
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != '/')
        {
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        }
        if (relative == "." || Path.IsPathRooted(relative))
        {
            return null;
        }

        var names = relative.Split('/');
        return names.Any(n => n.Length == 0 || n.StartsWith('.')) ? null : relative;
    }

    private bool AddPath(string fullPath, bool moved)
    {
        if (Relative(_root, fullPath) is not { } path)
        {
            return false;
        }

        _paths.Add(path);
        if (moved)
        {
            _moved.Add(path);
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
