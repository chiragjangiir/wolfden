using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// Keeps a den's index up to date while its files are edited outside
/// Wolfden, in an editor or by an AI agent (#14, design.md §7). It listens
/// to the den store's change feed (<see cref="IDenChangeFeed"/>, #121),
/// which for a den on disk watches the den folder, collects the file events of a burst of saves into one
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
/// Events are raised on the watcher's own thread, one at a time. Nothing
/// stops the watcher but <see cref="Dispose"/>: a failed batch is retried
/// as a rebuild, and if the operating system stops reporting file events
/// (a drive that went away, access denied, too many folders to watch), the
/// watcher starts listening again and rebuilds. Each of these is reported
/// through <see cref="Failed"/>, and retried after a pause that grows while
/// failures repeat.
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

    private readonly string _root;
    private readonly FileSystemDenStore _store;
    private readonly object _gate = new();
    private readonly ChangeBatcher _batcher;
    private readonly IndexUpdater _updater;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private IDisposable? _watch;
    private Observer? _observer;
    private FileStream? _lock;
    private Thread? _thread;
    private int _disposed;

    // Set when the OS stopped reporting events: why, and when to listen again.
    private Exception? _broken;
    private DateTime? _restartAt;
    private TimeSpan? _restartPauseValue;
    private DateTime? _listeningSince;

    // The pause before the next attempt to listen again.
    private TimeSpan NextRestartPause
    {
        get => _restartPauseValue ?? FirstPause;
        set => _restartPauseValue = value;
    }

    /// <summary>A watcher for the den at <paramref name="denRoot"/>. Call <see cref="Start"/> to start it.</summary>
    /// <param name="denRoot">The den's folder.</param>
    /// <param name="quiet">How long a batch waits for more events; <see cref="DefaultQuiet"/> if null.</param>
    /// <param name="maxDelay">The longest a batch waits while events keep coming; <see cref="DefaultMaxDelay"/> if null.</param>
    public DenWatcher(string denRoot, TimeSpan? quiet = null, TimeSpan? maxDelay = null)
        : this(new FileSystemDenStore(denRoot ?? throw new ArgumentNullException(nameof(denRoot))), quiet, maxDelay)
    {
    }

    /// <summary>
    /// A watcher for the den in <paramref name="store"/>, listening to its
    /// change feed, so a host that writes through the store can share it
    /// with the watcher. Call <see cref="Start"/> to start it.
    /// </summary>
    /// <param name="store">The den's store.</param>
    /// <param name="quiet">How long a batch waits for more events; <see cref="DefaultQuiet"/> if null.</param>
    /// <param name="maxDelay">The longest a batch waits while events keep coming; <see cref="DefaultMaxDelay"/> if null.</param>
    public DenWatcher(FileSystemDenStore store, TimeSpan? quiet = null, TimeSpan? maxDelay = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _root = store.Root;
        _batcher = new ChangeBatcher(_root, quiet ?? DefaultQuiet, maxDelay ?? DefaultMaxDelay);
        _updater = new IndexUpdater(_root);
        Changes = store;
    }

    /// <summary>Raised after each batch that changed what the index holds. <see cref="Start"/> returns the first build instead.</summary>
    public event EventHandler<IndexUpdate>? Updated;

    /// <summary>
    /// Raised when something goes wrong that the watcher works around: a
    /// batch that fails, such as when the index stays locked by a long
    /// query in another process; file events that stopped, so the watcher
    /// listens again; or an <see cref="Updated"/> handler that threw. The
    /// message says what happened and what the watcher does next.
    /// </summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The den's folder, as a full path.</summary>
    public string DenRoot => _root;

    /// <summary>The den's store, whose change feed the watcher listens to.</summary>
    public FileSystemDenStore Store => _store;

    /// <summary>The pause before the first retry after a failure. Tests shorten it.</summary>
    internal TimeSpan FirstPause { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest pause before trying again while failures repeat. Tests shorten it.</summary>
    internal TimeSpan MaxPause { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a new watch must run without an error before the pause
    /// before listening again starts from <see cref="FirstPause"/> again.
    /// Until then, each break waits longer, up to <see cref="MaxPause"/>.
    /// Tests shorten it.
    /// </summary>
    internal TimeSpan HealthyAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The change feed listened to: the store's own. Tests replace it to fail on purpose.</summary>
    internal IDenChangeFeed Changes { get; init; }

    /// <summary>Called before each batch is applied. Tests use it to make a batch fail.</summary>
    internal Action<ChangeBatch>? BeforeApply { get; init; }

    /// <summary>The updater, so tests can set its limits.</summary>
    internal IndexUpdater Updater => _updater;

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
            Listen();
            lock (_gate)
            {
                _listeningSince = DateTime.UtcNow;
            }

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

    /// <summary>
    /// Reports an error from the file system watcher, as its
    /// <see cref="FileSystemWatcher.Error"/> event does, so tests can fake one.
    /// </summary>
    internal void ReportWatcherError(Exception error)
    {
        if (error is InternalBufferOverflowException)
        {
            // Events were lost, but the watch goes on: rebuild.
            Add(b => b.AddOverflow(DateTime.UtcNow));
            return;
        }

        // The watch itself broke (on Windows it stops for good; on Linux new
        // folders may go unwatched): listen again, on the watcher's thread,
        // after a pause. A watch that breaks again soon after it was made
        // (Linux at its limit of folder watches) waits longer each time; one
        // that ran without trouble for HealthyAfter starts from FirstPause.
        lock (_gate)
        {
            _broken = error;
            if (_restartAt is not null)
            {
                // A restart is already on its way: no second one, no second report.
                return;
            }

            var now = DateTime.UtcNow;
            if (_listeningSince is { } since && now - since >= HealthyAfter)
            {
                NextRestartPause = FirstPause;
            }

            LastRestartPause = NextRestartPause;
            _restartAt = now + NextRestartPause;
            NextRestartPause = NextPause(NextRestartPause, MaxPause);
        }

        Wake();
    }

    /// <summary>How long the watcher waited, or will wait, before its latest attempt to listen again. For tests.</summary>
    internal TimeSpan LastRestartPause { get; private set; }

    /// <summary>The pause after <paramref name="pause"/> while failures repeat: twice as long, up to <paramref name="max"/>.</summary>
    internal static TimeSpan NextPause(TimeSpan pause, TimeSpan max) => pause * 2 > max ? max : pause * 2;

    private void Stop()
    {
        _stop.Cancel();
        Wake();
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join();
        }

        _thread = null;
        lock (_gate)
        {
            DisposeWatcher();
        }

        // The lock file stays: only an open one locks, and deleting it would
        // let a second watcher open a new file at once on Linux.
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>
    /// True if a watcher, in this process or another, is watching the den at
    /// <paramref name="denRoot"/>: it holds the den's watch lock open. A
    /// query uses it to wait briefly for the watcher instead of rebuilding
    /// the index itself (<see cref="IndexBuilder.EnsureUpToDate(string)"/>).
    /// </summary>
    public static bool IsWatching(string denRoot)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        var path = Path.Combine(Path.GetFullPath(denRoot), IndexBuilder.StateFolder, LockFileName);
        try
        {
            // Opening it with sharing fails only while a watcher holds it with none.
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private void TakeLock()
    {
        var folder = Path.Combine(_root, IndexBuilder.StateFolder);
        Directory.CreateDirectory(folder);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _lock = new FileStream(Path.Combine(folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException e)
            {
                if (attempt >= 3)
                {
                    throw new InvalidOperationException($"Another Wolfden process is already watching the den at '{_root}'.", e);
                }

                // A query looking to see whether a watcher runs (IsWatching) holds the file for a moment.
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Starts a new watch of the store's change feed, replacing any old one.</summary>
    private void Listen()
    {
        lock (_gate)
        {
            DisposeWatcher();
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            var observer = new Observer(this);
            _watch = Changes.Watch(observer);
            _observer = observer;
        }
    }

    private void DisposeWatcher()
    {
        _observer = null;
        if (_watch is { } watch)
        {
            _watch = null;
            watch.Dispose();
        }
    }

    private void OnBroken(Observer from, Exception error)
    {
        lock (_gate)
        {
            // An old watch's last word, after it was replaced: not this watch's problem.
            if (!ReferenceEquals(from, _observer))
            {
                return;
            }
        }

        ReportWatcherError(error);
    }

    private void Add(Action<ChangeBatcher> add)
    {
        lock (_gate)
        {
            add(_batcher);
        }

        Wake();
    }

    private void Wake()
    {
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
        var pause = FirstPause;
        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            DateTime? restartAt;
            TimeSpan? due;
            ChangeBatch? batch;
            lock (_gate)
            {
                restartAt = _restartAt;
                batch = restartAt <= now ? null : _batcher.Take(now);
                due = _batcher.DueIn(now);
            }

            if (restartAt <= now)
            {
                Restart();
                continue;
            }

            if (batch is null)
            {
                var wait = due ?? Timeout.InfiniteTimeSpan;
                if (restartAt is { } at && (wait == Timeout.InfiniteTimeSpan || at - now < wait))
                {
                    wait = at - now;
                }

                _wake.WaitOne(wait);
                continue;
            }

            IndexUpdate? update;
            try
            {
                BeforeApply?.Invoke(batch);
                update = _updater.Apply(batch, out var retry);
                if (retry.Count > 0)
                {
                    lock (_gate)
                    {
                        _batcher.AddAgain(retry, DateTime.UtcNow + RetryDelay - _batcher.Quiet);
                    }
                }

                pause = FirstPause;
            }
            catch (Exception e)
            {
                // A watcher must outlive a bad batch: rebuild after a pause.
                _updater.Invalidate();
                lock (_gate)
                {
                    _batcher.AddOverflow(DateTime.UtcNow + pause - _batcher.Quiet);
                }

                Report(new IOException(
                    $"couldn't update the index: {e.Message.TrimEnd('.')}. Trying again with a rebuild in {Seconds(pause)}.", e));
                pause = NextPause(pause, MaxPause);
                continue;
            }

            if (update is not null && !token.IsCancellationRequested)
            {
                try
                {
                    Updated?.Invoke(this, update);
                }
                catch (Exception e)
                {
                    // The host's handler failed, not the batch: the index is fine.
                    Report(new InvalidOperationException($"a handler of the watcher's updates failed: {e.Message}", e));
                }
            }
        }
    }

    /// <summary>
    /// Listens for file events again after the watch broke, and rebuilds;
    /// or, if that fails, tries again after a longer pause. Reports once per
    /// attempt. The pause isn't reset here: only a watch that then runs
    /// without trouble for <see cref="HealthyAfter"/> starts the next break
    /// from <see cref="FirstPause"/>.
    /// </summary>
    private void Restart()
    {
        Exception? why;
        lock (_gate)
        {
            why = _broken;
        }

        try
        {
            Listen();
        }
        catch (Exception e)
        {
            TimeSpan pause;
            lock (_gate)
            {
                pause = NextRestartPause;
                LastRestartPause = pause;
                _restartAt = DateTime.UtcNow + pause;
                NextRestartPause = NextPause(pause, MaxPause);
            }

            Report(new IOException(
                $"file events stopped ({why?.Message.TrimEnd('.')}), and listening again failed: {e.Message.TrimEnd('.')}. Trying again in {Seconds(pause)}; until then the index may fall behind the files.", e));
            return;
        }

        lock (_gate)
        {
            _broken = null;
            _restartAt = null;
            _listeningSince = DateTime.UtcNow;
            _batcher.AddOverflow(DateTime.UtcNow - _batcher.Quiet);
        }

        Report(new IOException($"file events stopped ({why?.Message.TrimEnd('.')}). Listening again, and rebuilding the index in case changes were missed.", why));
    }

    private void Report(Exception e)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        try
        {
            Failed?.Invoke(this, e);
        }
        catch (Exception)
        {
            // Nothing more to report it to; the watcher goes on.
        }
    }

    private static string Seconds(TimeSpan pause) =>
        pause.TotalSeconds >= 1 ? FormattableString.Invariant($"{pause.TotalSeconds:0.#} s") : FormattableString.Invariant($"{pause.TotalMilliseconds:0} ms");

    /// <summary>What one watch of the change feed reports to.</summary>
    private sealed class Observer(DenWatcher watcher) : IDenChangeObserver
    {
        public void Changed(DenChange change) => watcher.Add(b => b.Add(change, DateTime.UtcNow));

        public void Lost() => watcher.Add(b => b.AddOverflow(DateTime.UtcNow));

        public void Broken(Exception error) => watcher.OnBroken(this, error);
    }
}
