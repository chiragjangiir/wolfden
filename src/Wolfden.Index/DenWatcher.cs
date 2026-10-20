namespace Wolfden.Index;

/// <summary>
/// Keeps a den's index up to date while its files are edited outside
/// Wolfden, in an editor or by an AI agent (#14, design.md §7). It watches
/// the den folder, collects the file events of a burst of saves into one
/// batch, and brings the index up to date from it: reading only the changed
/// files where that's safe, and rebuilding the index where it isn't
/// (see <see cref="IndexUpdater"/>). A host such as <c>wolfden index --watch</c>,
/// and later <c>wolfden serve</c> and the MCP server, runs one per den.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> rebuilds the index first, so it starts from a known
/// state. Queries can read the index at any time: each batch is written in
/// one transaction, so they see it before the batch or after it. Only one
/// watcher can run per den, across processes; a second one fails to start.
/// </para>
/// <para>
/// Events and errors are raised on the watcher's own thread, one at a time.
/// A failed batch (<see cref="Failed"/>) doesn't stop the watcher: the next
/// batch rebuilds the index.
/// </para>
/// </remarks>
public sealed class DenWatcher : IDisposable
{
    /// <summary>The name of the file in <c>.wolfden/</c> that a running watcher holds open, so a second one can't start.</summary>
    public const string LockFileName = "watch.lock";

    /// <summary>How long a batch waits for more events before it's applied.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(300);

    /// <summary>The longest a batch waits after its first event while events keep coming.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(3);

    /// <summary>How soon a file that couldn't be read, because it was still being written, is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest pause before trying again after failures.</summary>
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);

    private readonly string _root;
    private readonly object _gate = new();
    private readonly ChangeBatcher _batcher;
    private readonly IndexUpdater _updater;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private FileSystemWatcher? _watcher;
    private FileStream? _lock;
    private Thread? _thread;
    private int _disposed;

    /// <summary>A watcher for the den at <paramref name="denRoot"/>. Call <see cref="Start"/> to start it.</summary>
    /// <param name="denRoot">The den's folder.</param>
    /// <param name="quiet">How long a batch waits for more events; <see cref="DefaultQuiet"/> if null.</param>
    /// <param name="maxDelay">The longest a batch waits while events keep coming; <see cref="DefaultMaxDelay"/> if null.</param>
    public DenWatcher(string denRoot, TimeSpan? quiet = null, TimeSpan? maxDelay = null)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        _root = Path.GetFullPath(denRoot);
        _batcher = new ChangeBatcher(_root, quiet ?? DefaultQuiet, maxDelay ?? DefaultMaxDelay);
        _updater = new IndexUpdater(_root);
    }

    /// <summary>Raised after each batch that changed what the index holds. <see cref="Start"/> returns the first build instead.</summary>
    public event EventHandler<IndexUpdate>? Updated;

    /// <summary>
    /// Raised when a batch fails, such as when the index stays locked by a
    /// long query in another process. The watcher tries again with a
    /// rebuild, after a pause that grows while failures repeat.
    /// </summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The den's folder, as a full path.</summary>
    public string DenRoot => _root;

    /// <summary>
    /// Starts watching: takes the den's watch lock, starts listening for
    /// file events, rebuilds the index, and then applies batches until
    /// disposed. Returns the first build. Events that arrive during it are
    /// applied after it.
    /// </summary>
    /// <exception cref="InvalidOperationException">It's already started, or another watcher, in this process or another, is watching the den.</exception>
    /// <exception cref="IOException">The index couldn't be built (see <see cref="IndexBuilder.Rebuild"/>).</exception>
    public IndexUpdate Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_thread is not null)
        {
            throw new InvalidOperationException("The watcher is already started.");
        }

        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"The den folder '{_root}' doesn't exist.");
        }

        try
        {
            TakeLock();

            // Listen before the first build reads anything, so no edit is missed.
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += OnEvent;
            _watcher.Changed += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;

            var first = _updater.Rebuild("the watcher started");
            _thread = new Thread(Run) { IsBackground = true, Name = "Wolfden index watcher" };
            _thread.Start();
            return first;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Stops watching and waits for a batch in progress to finish. The index is left as the last batch wrote it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        _wake.Dispose();
        _stop.Dispose();
    }

    private void Stop()
    {
        _stop.Cancel();
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watcher = null;
        }

        _wake.Set();
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join();
        }

        _thread = null;
        if (_lock is { } held)
        {
            held.Dispose();
            _lock = null;
            try
            {
                File.Delete(held.Name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover lock file is harmless, since only an open one locks.
            }
        }
    }

    private void TakeLock()
    {
        var folder = Path.Combine(_root, IndexBuilder.StateFolder);
        Directory.CreateDirectory(folder);
        try
        {
            _lock = new FileStream(Path.Combine(folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Another Wolfden process is already watching the den at '{_root}'.", e);
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Add(b => b.Add(e.ChangeType, e.FullPath, null, DateTime.UtcNow));

    private void OnRenamed(object sender, RenamedEventArgs e) => Add(b => b.Add(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath, DateTime.UtcNow));

    // The buffer overflowed (InternalBufferOverflowException) or the watch broke: events were lost.
    private void OnError(object sender, ErrorEventArgs e) => Add(b => b.AddOverflow(DateTime.UtcNow));

    private void Add(Action<ChangeBatcher> add)
    {
        lock (_gate)
        {
            add(_batcher);
        }

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // An event that raced with Dispose.
        }
    }

    private void Run()
    {
        var token = _stop.Token;
        var pause = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            TimeSpan? due;
            ChangeBatch? batch;
            lock (_gate)
            {
                batch = _batcher.Take(DateTime.UtcNow);
                due = _batcher.DueIn(DateTime.UtcNow);
            }

            if (batch is null)
            {
                _wake.WaitOne(due ?? Timeout.InfiniteTimeSpan);
                continue;
            }

            try
            {
                var update = _updater.Apply(batch, out var retry);
                if (retry.Count > 0)
                {
                    lock (_gate)
                    {
                        _batcher.AddAgain(retry, DateTime.UtcNow + RetryDelay - _batcher.Quiet);
                    }
                }

                pause = TimeSpan.FromSeconds(1);
                if (update is not null && !token.IsCancellationRequested)
                {
                    Updated?.Invoke(this, update);
                }
            }
            catch (Exception e)
            {
                // A watcher must outlive a bad batch: rebuild after a pause.
                _updater.Invalidate();
                lock (_gate)
                {
                    _batcher.AddOverflow(DateTime.UtcNow + pause - _batcher.Quiet);
                }

                pause = pause * 2 > MaxPause ? MaxPause : pause * 2;
                if (!token.IsCancellationRequested)
                {
                    Failed?.Invoke(this, e);
                }
            }
        }
    }
}
