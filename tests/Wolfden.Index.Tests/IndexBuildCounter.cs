namespace Wolfden.Index.Tests;

/// <summary>
/// Counts <see cref="IndexBuilder"/>'s full rebuilds of one den, and its
/// waits for that den's watcher, from when it's made until it's disposed,
/// through the builder's test seams (#130). Other dens' events, from tests
/// running in parallel, aren't counted. Wolfden.Mcp.Tests links this file.
/// </summary>
internal sealed class IndexBuildCounter : IDisposable
{
    private readonly string _root;
    private int _rebuilds;
    private int _watcherWaits;

    public IndexBuildCounter(string denRoot)
    {
        _root = Path.GetFullPath(denRoot);
        IndexBuilder.Rebuilding += OnRebuilding;
        IndexBuilder.WaitingForWatcher += OnWaitingForWatcher;
    }

    /// <summary>The full rebuilds of the den so far.</summary>
    public int Rebuilds => Volatile.Read(ref _rebuilds);

    /// <summary>The times a call has started waiting for the den's watcher so far.</summary>
    public int WatcherWaits => Volatile.Read(ref _watcherWaits);

    public void Dispose()
    {
        IndexBuilder.Rebuilding -= OnRebuilding;
        IndexBuilder.WaitingForWatcher -= OnWaitingForWatcher;
    }

    private void OnRebuilding(string root)
    {
        if (string.Equals(root, _root, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _rebuilds);
        }
    }

    private void OnWaitingForWatcher(string root)
    {
        if (string.Equals(root, _root, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _watcherWaits);
        }
    }
}
