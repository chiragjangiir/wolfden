using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wolfden.Core.Structure;

namespace Wolfden.Core.History;

/// <summary>
/// Automatic checkpoints (format.md F-DEN-9, design.md §6, #30): with
/// <c>auto_checkpoint</c> set in <c>den.yaml</c>, a running
/// <c>wolfden serve</c> makes a checkpoint once the den's files have gone
/// that long without a change, with a generated message
/// (<see cref="Message"/>). It's off unless set.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="PollInterval"/> it looks at the den's files (their
/// paths, sizes, and times, not their contents, and never hidden ones,
/// F-LAY-2). A change restarts the wait. Once the files have been still
/// for the setting's time, it asks the history what a checkpoint would
/// record, and records it. Then it waits for the next change: it makes
/// one checkpoint per quiet spell, never one per poll.
/// </para>
/// <para>
/// <b>Never during a git write.</b> While the history says a write may be
/// running (<see cref="IHistoryProvider.IsWriting"/>: a checkpoint in this
/// process, a git Wolfden stopped waiting for, or another program's
/// <c>index.lock</c>), it doesn't start one, and tries again on the next
/// poll. It never stops git (#145).
/// </para>
/// <para>
/// <b>Two processes on one den.</b> The per-repository gate is in-process
/// only, so another Wolfden may checkpoint the same den at the same time.
/// Nothing to record does nothing, and a checkpoint git refuses (another
/// program holding the lock, or a hook) is reported once and tried again
/// after another quiet spell, never in a tight loop.
/// </para>
/// <para>
/// <b>Without history</b> the setting does nothing apart from one warning.
/// The setting is read from <c>den.yaml</c> on each poll, so turning it on
/// or off needs no restart.
/// </para>
/// </remarks>
public sealed class AutoCheckpointer : IDisposable
{
    /// <summary>How often the den's files are looked at.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The start of every automatic checkpoint's message, so the history tells them apart.</summary>
    public const string MessagePrefix = "Auto-checkpoint: ";

    /// <summary>The most files a message lists by name.</summary>
    public const int MostListed = 20;

    private readonly string _denRoot;
    private readonly Func<IHistoryProvider> _history;
    private readonly CheckpointLedger _ledger;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Lock _tickLock = new();
    private ITimer? _timer;

    private string? _print;
    private DateTimeOffset _lastChange;
    private bool _settled;
    private string? _problemSaid;
    private bool _noHistorySaid;

    /// <summary>Automatic checkpoints for the den at <paramref name="denRoot"/>.</summary>
    /// <param name="denRoot">The den's folder.</param>
    /// <param name="history">Finds the den's history, on each attempt, so a den that becomes a repository gets history without a restart.</param>
    /// <param name="ledger">The host's ledger, so its own saves are told apart afterwards as for a checkpoint the writer makes.</param>
    /// <param name="time">The clock and timers.</param>
    /// <param name="log">Where to say what it did and what went wrong, a line at a time.</param>
    public AutoCheckpointer(string denRoot, Func<IHistoryProvider> history, CheckpointLedger ledger, TimeProvider time, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _denRoot = Path.GetFullPath(denRoot);
        _history = history;
        _ledger = ledger;
        _time = time;
        _log = log;
        Fingerprint = () => FilesFingerprint(_denRoot);
        Setting = () => ReadSetting(_denRoot);
    }

    /// <summary>What the den's files look like now; any change to it is a change to the den. Tests replace it.</summary>
    public Func<string> Fingerprint { get; init; }

    /// <summary>The <c>auto_checkpoint</c> setting now. Tests replace it.</summary>
    public Func<AutoCheckpointSetting> Setting { get; init; }

    /// <summary>
    /// Starts polling, from now. A setting that isn't valid, or one set in
    /// a den without history, is said straight away.
    /// </summary>
    public void Start()
    {
        lock (_tickLock)
        {
            _print = Fingerprint();
            _lastChange = _time.GetUtcNow();
            var setting = Setting();
            _problemSaid = setting.Problem;
            if (setting.Problem is { } problem)
            {
                _log($"den.yaml: {problem} (F-DEN-9)");
            }
            else if (setting.Every is { } every && !_history().Capabilities.HasFlag(HistoryCapabilities.Checkpoint))
            {
                NoHistory(_lastChange, every);
            }
        }

        _timer ??= _time.CreateTimer(_ => SafeTick(), null, PollInterval, PollInterval);
    }

    /// <summary>Stops polling. A checkpoint under way runs to its end: git is never stopped.</summary>
    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// One poll: notes whether the files changed, and makes a checkpoint if
    /// they've been still long enough. The timer calls it; tests call it
    /// with a clock of their own. A poll that finds another still running
    /// does nothing.
    /// </summary>
    public AutoCheckpointOutcome Tick()
    {
        if (!_tickLock.TryEnter())
        {
            return AutoCheckpointOutcome.Busy;
        }

        try
        {
            var now = _time.GetUtcNow();
            var print = Fingerprint();
            if (print != _print)
            {
                _print = print;
                _lastChange = now;
                _settled = false;
            }

            var setting = Setting();
            if (setting.Problem != _problemSaid)
            {
                _problemSaid = setting.Problem;
                if (setting.Problem is { } problem)
                {
                    _log($"den.yaml: {problem} (F-DEN-9)");
                }
            }

            if (setting.Every is not { } every)
            {
                return AutoCheckpointOutcome.Off;
            }

            if (_settled)
            {
                return AutoCheckpointOutcome.Settled;
            }

            if (now - _lastChange < every)
            {
                return AutoCheckpointOutcome.Waiting;
            }

            var history = _history();
            if (!history.Capabilities.HasFlag(HistoryCapabilities.Checkpoint))
            {
                return NoHistory(now, every);
            }

            _noHistorySaid = false;
            if (history.IsWriting)
            {
                return AutoCheckpointOutcome.Busy;
            }

            try
            {
                var pending = history.Pending();
                if (!pending.IsAvailable)
                {
                    return NoHistory(now, every);
                }

                if (pending.Value.Count == 0)
                {
                    _settled = true;
                    return AutoCheckpointOutcome.NothingToRecord;
                }

                var done = _ledger.Checkpoint(history, new CheckpointRequest(Message(pending.Value)));
                if (!done.Result.IsAvailable)
                {
                    return NoHistory(now, every);
                }

                _settled = true;
                if (done.Result.Value is not { } entry)
                {
                    return AutoCheckpointOutcome.NothingToRecord;
                }

                _log($"Made an automatic checkpoint after {AutoCheckpoint.Format(every)} without changes: {FirstLine(entry.Message)} ({ShortId(entry.Id)}).");
                return AutoCheckpointOutcome.Recorded;
            }
            catch (HistoryException e)
            {
                // Another program may hold the lock, or a hook refused: wait for another quiet spell.
                _lastChange = now;
                _log($"Couldn't make an automatic checkpoint: {e.Message} Wolfden will try again after {AutoCheckpoint.Format(every)} more without changes.");
                return AutoCheckpointOutcome.Failed;
            }
        }
        finally
        {
            _tickLock.Exit();
        }
    }

    private AutoCheckpointOutcome NoHistory(DateTimeOffset now, TimeSpan every)
    {
        // Look again after another quiet spell, not on every poll.
        _lastChange = now;
        if (!_noHistorySaid)
        {
            _noHistorySaid = true;
            _log(
                "den.yaml sets auto_checkpoint, but the den has no history, so no automatic checkpoints are made. " +
                "To keep history, run 'git init' in the den's folder (not in a synced folder such as Dropbox, OneDrive, or iCloud).");
        }

        return AutoCheckpointOutcome.NoHistory;
    }

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A poll mustn't take the server down; the next one tries again.
            _log($"Automatic checkpoints: {e.Message}");
        }
    }

    /// <summary>
    /// The message of an automatic checkpoint: <c>Auto-checkpoint: edited 3
    /// notes</c> when every change is an edit, otherwise <c>Auto-checkpoint:
    /// changed 4 notes (2 edited, 1 added, 1 deleted)</c>; then a blank line
    /// and one line per file (<c>edited universes/…</c>), at most
    /// <see cref="MostListed"/>, and how many more. A note here is any den
    /// file.
    /// </summary>
    public static string Message(IReadOnlyList<HistoryFileChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var counts = new[] { HistoryChangeKind.Modified, HistoryChangeKind.Added, HistoryChangeKind.Deleted, HistoryChangeKind.Renamed }
            .Select(k => (Kind: k, Count: changes.Count(c => c.Kind == k)))
            .Where(k => k.Count > 0)
            .ToList();
        var notes = Notes(changes.Count);
        var subject = counts is [{ Kind: HistoryChangeKind.Modified }]
            ? $"{MessagePrefix}edited {notes}"
            : $"{MessagePrefix}changed {notes} ({string.Join(", ", counts.Select(k => $"{k.Count} {Verb(k.Kind)}"))})";

        var body = new StringBuilder(subject).Append("\n\n");
        foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal).Take(MostListed))
        {
            body.Append(Verb(change.Kind)).Append(' ');
            body.Append(change.OldPath is { } old ? $"{old} -> {change.Path}" : change.Path).Append('\n');
        }

        if (changes.Count > MostListed)
        {
            body.Append(CultureInfo.InvariantCulture, $"and {changes.Count - MostListed} more\n");
        }

        return body.ToString().TrimEnd('\n');

        static string Notes(int n) => n == 1 ? "1 note" : string.Create(CultureInfo.InvariantCulture, $"{n} notes");

        static string Verb(HistoryChangeKind kind) => kind switch
        {
            HistoryChangeKind.Modified => "edited",
            HistoryChangeKind.Added => "added",
            HistoryChangeKind.Deleted => "deleted",
            _ => "renamed",
        };
    }

    /// <summary><c>auto_checkpoint</c> from the den's <c>den.yaml</c> as it is now; a file that can't be read leaves it off.</summary>
    public static AutoCheckpointSetting ReadSetting(string denRoot)
    {
        try
        {
            return AutoCheckpoint.Read(File.ReadAllBytes(Path.Combine(denRoot, "den.yaml")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AutoCheckpointSetting.Off;
        }
    }

    /// <summary>
    /// A fingerprint of the den's files: each one's path, size, and time of
    /// last change, hidden files and folders and folder links left out
    /// (F-LAY-2). It reads no file's contents.
    /// </summary>
    public static string FilesFingerprint(string denRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        var folders = new Stack<(string Full, string Relative)>();
        folders.Push((denRoot, ""));
        while (folders.Count > 0)
        {
            var (full, relative) = folders.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(full).EnumerateFileSystemInfos("*", options)
                    .Where(e => !e.Name.StartsWith('.'))
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var path = relative.Length == 0 ? entry.Name : $"{relative}/{entry.Name}";
                if (entry is DirectoryInfo folder)
                {
                    if (folder.LinkTarget is null)
                    {
                        folders.Push((folder.FullName, path));
                    }

                    continue;
                }

                var file = (FileInfo)entry;
                hash.AppendData(Encoding.UTF8.GetBytes(string.Create(
                    CultureInfo.InvariantCulture, $"{path}\0{SafeLength(file)}\0{file.LastWriteTimeUtc.Ticks}\n")));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());

        static long SafeLength(FileInfo file)
        {
            try
            {
                return file.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        }
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0];

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;
}

/// <summary>What one poll of <see cref="AutoCheckpointer.Tick"/> did.</summary>
public enum AutoCheckpointOutcome
{
    /// <summary><c>auto_checkpoint</c> isn't set, or isn't valid.</summary>
    Off,

    /// <summary>The files changed too recently.</summary>
    Waiting,

    /// <summary>This quiet spell already had its checkpoint, or had nothing to record; waiting for the next change.</summary>
    Settled,

    /// <summary>A git write may be under way, or another poll is running; tried again on the next poll.</summary>
    Busy,

    /// <summary>The den has no history.</summary>
    NoHistory,

    /// <summary>Nothing changed since the last checkpoint.</summary>
    NothingToRecord,

    /// <summary>A checkpoint was made.</summary>
    Recorded,

    /// <summary>The history refused or failed; tried again after another quiet spell.</summary>
    Failed,
}
