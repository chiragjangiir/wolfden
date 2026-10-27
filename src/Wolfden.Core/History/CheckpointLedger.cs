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
/// It's safe to use from several threads. A host that must keep a write
/// from landing in the middle of a checkpoint, as the MCP tools do, holds
/// its own write lock around both.
/// </para>
/// </remarks>
public sealed class CheckpointLedger
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Lock _lock = new();

    /// <summary>Each path's last written bytes; null marks a file the host deleted (or moved away).</summary>
    private readonly Dictionary<string, byte[]?> _written = new(PathComparer);

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
            _written[path] = bytes;
        }
    }

    /// <summary>Records that the host deleted the file at <paramref name="path"/>, such as the old path of a move.</summary>
    public void Deleted(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_lock)
        {
            _written[path] = null;
        }
    }

    /// <summary>The paths the ledger holds as the host's own deletions, in ordinal order.</summary>
    public IReadOnlyList<string> Deletions
    {
        get
        {
            lock (_lock)
            {
                return _written.Where(p => p.Value is null).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>
    /// True if <paramref name="change"/> is the host's own and nothing else
    /// has changed the file since: it holds what the host last wrote, or
    /// the host deleted it and it's still absent. A rename counts when the
    /// host wrote its new path or deleted its old one.
    /// </summary>
    public bool IsOwn(HistoryFileChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            if (_written.TryGetValue(change.Path, out var bytes))
            {
                var now = Store.TryReadFile(change.Path);
                return bytes is null ? now is null : now is not null && now.AsSpan().SequenceEqual(bytes);
            }

            return change.OldPath is { } old && _written.TryGetValue(old, out var was) && was is null && !Store.FileExists(old);
        }
    }

    /// <summary>
    /// Makes a checkpoint through <paramref name="history"/>, and says which
    /// of the files it recorded the host wrote and which changed some other
    /// way. A file the host wrote and something else changed after is in
    /// both lists. Recorded files start afresh, so a later checkpoint lists
    /// only what was written since.
    /// </summary>
    /// <exception cref="ArgumentException">The request isn't one the backend takes, such as a message with control characters.</exception>
    /// <exception cref="HistoryException">The backend failed: git refused the commit, or didn't finish in time.</exception>
    public LedgerCheckpoint Checkpoint(IHistoryProvider history, CheckpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            // Before recording: forget the writes that are recorded already (the writer
            // committed them elsewhere), and note what each tracked file holds now, which is
            // what the checkpoint records.
            var onDisk = new Dictionary<string, byte[]?>(PathComparer);
            foreach (var (path, bytes) in _written.ToList())
            {
                if (bytes is null)
                {
                    // Deleted by the host. A file there again was put there some other way,
                    // so the deletion isn't the host's to claim any more.
                    onDisk[path] = Store.TryReadFile(path);
                    if (onDisk[path] is not null)
                    {
                        _written.Remove(path);
                    }
                }
                else if (history.HoldsAtLatest(path, bytes, cancellationToken) is { IsAvailable: true, Value: true })
                {
                    _written.Remove(path);
                }
                else
                {
                    onDisk[path] = Store.TryReadFile(path);
                }
            }

            var result = history.Checkpoint(request, cancellationToken);
            if (!result.IsAvailable)
            {
                return new LedgerCheckpoint(result, [], []);
            }

            if (result.Value is not { } e)
            {
                ForgetRecordedDeletions(request.Paths);
                return new LedgerCheckpoint(result, [], []);
            }

            var ours = new List<string>();
            var theirs = new List<string>();
            foreach (var change in e.Changes)
            {
                var tracked = _written.ContainsKey(change.Path) ? change.Path
                    : change.OldPath is { } was && _written.ContainsKey(was) ? was : null;
                if (tracked is null)
                {
                    theirs.Add(change.Path);
                    continue;
                }

                ours.Add(change.Path);
                var now = change.Path == tracked ? onDisk.GetValueOrDefault(tracked) : Store.TryReadFile(change.Path);
                var wrote = _written[tracked];
                if (wrote is null ? now is not null : now is null || !now.AsSpan().SequenceEqual(wrote))
                {
                    theirs.Add(change.Path);
                }
            }

            foreach (var change in e.Changes)
            {
                _written.Remove(change.Path);
                if (change.OldPath is { } old)
                {
                    _written.Remove(old);
                }
            }

            ForgetRecordedDeletions(request.Paths);
            return new LedgerCheckpoint(result, ours.Order(StringComparer.Ordinal).ToList(), theirs.Order(StringComparer.Ordinal).ToList());
        }
    }

    /// <summary>
    /// After a checkpoint: forgets the host's deletions that the latest
    /// recorded state no longer holds. A checkpoint records every den file
    /// it covers (the whole den, or <paramref name="given"/>), so a path it
    /// covered that's absent now is absent at the latest checkpoint too,
    /// whether this one recorded the deletion or an earlier commit made
    /// elsewhere did.
    /// </summary>
    private void ForgetRecordedDeletions(IReadOnlyList<string>? given)
    {
        foreach (var (path, bytes) in _written.ToList())
        {
            var covered = given is null || given.Any(p => string.Equals(path, p, PathComparison) || path.StartsWith(p + "/", PathComparison));
            if (bytes is null && covered && !Store.FileExists(path))
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
