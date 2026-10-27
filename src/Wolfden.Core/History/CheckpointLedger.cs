using Wolfden.Core.Structure;

namespace Wolfden.Core.History;

/// <summary>
/// The den files one Wolfden host saved since a checkpoint last recorded
/// them, each with the bytes it last wrote, so a checkpoint can tell the
/// host's own edits from changes made some other way (design.md §6, #30).
/// The MCP server keeps one per session (<c>written_by_session</c> and
/// <c>changed_outside_session</c>, maintainer decision 2026-10-25), and
/// <c>wolfden serve</c> one for the web UI's saves.
/// </summary>
/// <remarks>
/// <para>
/// Paths are den-relative and compare as the file system does: ignoring
/// case on Windows and macOS, where git's paths may differ in case.
/// </para>
/// <para>
/// <b>Never locked across git.</b> A checkpoint can wait minutes on git
/// (a signing passphrase prompt, a slow hook, #143), so the ledger's lock
/// is held only to take a snapshot before it and to reconcile after it,
/// never while git runs: a save that records itself (<see cref="Wrote"/>)
/// or a status that asks (<see cref="IsOwn"/>) doesn't wait for a
/// checkpoint. A write recorded while a checkpoint runs may or may not be
/// in it, so it stays tracked as the host's: the next checkpoint forgets
/// it if the history already holds it. A host that must keep a write from
/// landing in the middle of a checkpoint, as the MCP tools do, holds its
/// own write lock around both.
/// </para>
/// </remarks>
public sealed class CheckpointLedger
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Lock _lock = new();

    /// <summary>
    /// Each path's last written bytes (null marks a file the host deleted or
    /// moved away), and the number of the write that recorded it, so a
    /// checkpoint forgets an entry only if no write changed it meanwhile.
    /// </summary>
    private readonly Dictionary<string, (byte[]? Bytes, long Write)> _written = new(PathComparer);

    private long _writes;

    /// <summary>A ledger for writes that go through <paramref name="store"/>.</summary>
    public CheckpointLedger(IDenStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
    }

    /// <summary>The den store the host writes through, which the ledger reads to compare.</summary>
    public IDenStore Store { get; }

    /// <summary>Records that the host saved <paramref name="bytes"/> at <paramref name="path"/>.</summary>
    public void Wrote(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_lock)
        {
            _written[path] = (bytes, ++_writes);
        }
    }

    /// <summary>Records that the host deleted the file at <paramref name="path"/>, such as the old path of a move.</summary>
    public void Deleted(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_lock)
        {
            _written[path] = (null, ++_writes);
        }
    }

    /// <summary>The paths the ledger holds as the host's own deletions, in ordinal order.</summary>
    public IReadOnlyList<string> Deletions
    {
        get
        {
            lock (_lock)
            {
                return _written.Where(p => p.Value.Bytes is null).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>
    /// True if <paramref name="change"/> is the host's own and nothing else
    /// has changed the file since: it holds what the host last wrote, or
    /// the host deleted it and it's still absent. A rename counts when the
    /// host wrote its new path or deleted its old one. It reads the file
    /// outside the lock, so it never waits on a checkpoint.
    /// </summary>
    public bool IsOwn(HistoryFileChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        (byte[]? Bytes, long Write) entry;
        bool tracked;
        (byte[]? Bytes, long Write) old = default;
        bool oldTracked;
        lock (_lock)
        {
            tracked = _written.TryGetValue(change.Path, out entry);
            oldTracked = change.OldPath is { } o && _written.TryGetValue(o, out old);
        }

        if (tracked)
        {
            var now = Store.TryReadFile(change.Path);
            return entry.Bytes is null ? now is null : now is not null && now.AsSpan().SequenceEqual(entry.Bytes);
        }

        return oldTracked && old.Bytes is null && !Store.FileExists(change.OldPath!);
    }

    /// <summary>
    /// Makes a checkpoint through <paramref name="history"/>, and says which
    /// of the files it recorded the host wrote and which changed some other
    /// way. A file the host wrote and something else changed after is in
    /// both lists. Recorded files start afresh, so a later checkpoint lists
    /// only what was written since, except a file written again while the
    /// checkpoint ran, which stays the host's.
    /// </summary>
    /// <exception cref="ArgumentException">The request isn't one the backend takes, such as a message with control characters.</exception>
    /// <exception cref="HistoryException">The backend failed: git refused the commit, or didn't finish in time.</exception>
    public LedgerCheckpoint Checkpoint(IHistoryProvider history, CheckpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(request);

        // 1. A snapshot, under the lock.
        Dictionary<string, (byte[]? Bytes, long Write)> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, (byte[]? Bytes, long Write)>(_written, PathComparer);
        }

        // 2. Outside it: what each tracked file holds now, which is what the checkpoint
        // records, and which writes are recorded already (the writer committed them
        // elsewhere). A deleted file that's there again was put there some other way.
        var onDisk = new Dictionary<string, byte[]?>(PathComparer);
        var stale = new Dictionary<string, (byte[]? Bytes, long Write)>(PathComparer);
        foreach (var (path, (bytes, write)) in snapshot)
        {
            if (bytes is null)
            {
                onDisk[path] = Store.TryReadFile(path);
                if (onDisk[path] is not null)
                {
                    stale[path] = (bytes, write);
                }
            }
            else if (history.HoldsAtLatest(path, bytes, cancellationToken) is { IsAvailable: true, Value: true })
            {
                stale[path] = (bytes, write);
            }
            else
            {
                onDisk[path] = Store.TryReadFile(path);
            }
        }

        foreach (var path in stale.Keys)
        {
            snapshot.Remove(path);
        }

        // 3. The commit, outside the lock: it can wait minutes on a passphrase prompt.
        HistoryResult<HistoryEntry?> result;
        try
        {
            result = history.Checkpoint(request, cancellationToken);
        }
        finally
        {
            // What was found recorded already is forgotten either way, unless written again since.
            lock (_lock)
            {
                foreach (var path in stale.Keys)
                {
                    Forget(path, stale);
                }
            }
        }

        if (!result.IsAvailable)
        {
            return new LedgerCheckpoint(result, [], []);
        }

        if (result.Value is not { } e)
        {
            lock (_lock)
            {
                ForgetRecordedDeletions(request.Paths, snapshot);
            }

            return new LedgerCheckpoint(result, [], []);
        }

        // 4. Reconcile, under the lock, from the snapshot.
        var ours = new List<string>();
        var theirs = new List<string>();
        foreach (var change in e.Changes)
        {
            var tracked = snapshot.ContainsKey(change.Path) ? change.Path
                : change.OldPath is { } was && snapshot.ContainsKey(was) ? was : null;
            if (tracked is null)
            {
                theirs.Add(change.Path);
                continue;
            }

            ours.Add(change.Path);
            var now = change.Path == tracked ? onDisk.GetValueOrDefault(tracked) : Store.TryReadFile(change.Path);
            var wrote = snapshot[tracked].Bytes;
            if (wrote is null ? now is not null : now is null || !now.AsSpan().SequenceEqual(wrote))
            {
                theirs.Add(change.Path);
            }
        }

        lock (_lock)
        {
            foreach (var change in e.Changes)
            {
                Forget(change.Path, snapshot);
                if (change.OldPath is { } old)
                {
                    Forget(old, snapshot);
                }
            }

            ForgetRecordedDeletions(request.Paths, snapshot);
        }

        return new LedgerCheckpoint(result, ours.Order(StringComparer.Ordinal).ToList(), theirs.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Forgets <paramref name="path"/> after a checkpoint recorded it, unless
    /// a write recorded it again while the checkpoint ran: that write may not
    /// be in the checkpoint, so it stays the host's. Under the lock.
    /// </summary>
    private void Forget(string path, Dictionary<string, (byte[]? Bytes, long Write)> snapshot)
    {
        if (!_written.TryGetValue(path, out var now))
        {
            return;
        }

        // Tracked before the checkpoint and unchanged since: forget it. Not tracked
        // before it: written during it, so keep it.
        if (snapshot.TryGetValue(path, out var then) && then.Write == now.Write)
        {
            _written.Remove(path);
        }
    }

    /// <summary>
    /// After a checkpoint: forgets the host's deletions that the latest
    /// recorded state no longer holds. A checkpoint records every den file
    /// it covers (the whole den, or <paramref name="given"/>), so a path it
    /// covered that's absent now is absent at the latest checkpoint too,
    /// whether this one recorded the deletion or an earlier commit made
    /// elsewhere did. A deletion recorded while the checkpoint ran is kept.
    /// Under the lock.
    /// </summary>
    private void ForgetRecordedDeletions(IReadOnlyList<string>? given, Dictionary<string, (byte[]? Bytes, long Write)> snapshot)
    {
        foreach (var (path, (bytes, write)) in _written.ToList())
        {
            var covered = given is null || given.Any(p => string.Equals(path, p, PathComparison) || path.StartsWith(p + "/", PathComparison));
            if (bytes is null && covered && snapshot.TryGetValue(path, out var then) && then.Write == write && !Store.FileExists(path))
            {
                _written.Remove(path);
            }
        }
    }
}

/// <summary>What <see cref="CheckpointLedger.Checkpoint"/> did.</summary>
/// <param name="Result">The backend's result: unavailable, nothing to record (null), or the checkpoint.</param>
/// <param name="Written">Of the files recorded, the ones the host saved since the last checkpoint that recorded them.</param>
/// <param name="ChangedElsewhere">Of the files recorded, the ones that changed some other way, such as in the writer's editor.</param>
public sealed record LedgerCheckpoint(HistoryResult<HistoryEntry?> Result, IReadOnlyList<string> Written, IReadOnlyList<string> ChangedElsewhere);
