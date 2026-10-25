namespace Wolfden.Core.History;

/// <summary>
/// A den's history backend (design.md §6, #121): log, diffs, checkpoints,
/// and tags. History is a capability a backend provides, not part of the
/// format. Git is the v0 backend (<c>Wolfden.Git</c>, #19); a den without
/// one uses <see cref="NoHistory"/>, which still loads, validates, and
/// indexes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to the den.</b> A provider is made for one den, the folder
/// that holds <c>den.yaml</c>, which can be a subfolder of a larger
/// repository. Every path it takes or gives is den-relative with <c>/</c>,
/// as for the den store. The log and diffs cover only the den's files, a
/// checkpoint records only them, and nothing outside the den is staged,
/// changed, or reported.
/// </para>
/// <para>
/// <b>Den files only.</b> Hidden paths aren't part of the den (F-LAY-2), so
/// they're never recorded or reported: not <c>.wolfden/</c>, not an
/// editor's swap files, not a <c>.gitignore</c>.
/// </para>
/// <para>
/// <b>Checkpoints leave the rest of the repository alone.</b> A checkpoint
/// records the den's files and nothing else, even when the user had
/// already staged other changes in a larger repository: those stay staged,
/// and out of the checkpoint (for git, <c>git commit --only -- &lt;den paths&gt;</c>).
/// </para>
/// <para>
/// <b>Tags belong to the whole repository.</b> A backend's tags, such as
/// git's, aren't scoped to the den: a tag name is shared with everything
/// else in the repository, other dens in it included. <see cref="Tags"/>
/// lists them all, and a tag name must be chosen so it can't clash. How
/// freeze tags are named is decided with the freeze workflow.
/// </para>
/// <para>
/// <b>Unavailable isn't an error.</b> An operation the backend can't do,
/// because there's no backend or it lacks the capability
/// (<see cref="Capabilities"/>), returns a result that says so in words
/// for the writer (<see cref="HistoryResult{T}.Unavailable"/>), and never
/// throws for it. A backend that can do it but fails while doing so, such
/// as a git command that exits with an error, throws a
/// <see cref="HistoryException"/>.
/// </para>
/// <para>
/// <b>Revisions</b> are the backend's own ids (a commit hash for git) or
/// tag names, passed as strings and never parsed by the caller.
/// </para>
/// <para>
/// <b>Cancellation.</b> A backend that runs a process, such as git, stops
/// it when the token is cancelled and throws <see cref="OperationCanceledException"/>,
/// except a step that can't be stopped part way without harm, such as
/// git writing a commit: that runs to the end, and a checkpoint it records
/// is returned.
/// </para>
/// </remarks>
public interface IHistoryProvider
{
    /// <summary>The backend's short name for messages and results, such as <c>git</c>, or <c>none</c>.</summary>
    string Name { get; }

    /// <summary>What this backend can do for this den. <see cref="HistoryCapabilities.None"/> for a den without history.</summary>
    HistoryCapabilities Capabilities { get; }

    /// <summary>
    /// The checkpoints that changed the den, or <paramref name="query"/>'s
    /// file or folder, newest first.
    /// </summary>
    HistoryResult<IReadOnlyList<HistoryEntry>> Log(HistoryLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>What changed between two revisions, or between one and the files as they are now.</summary>
    HistoryResult<HistoryDiff> Diff(HistoryDiffQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the den's files as they are now, with a message: a commit,
    /// for git. Only den files are recorded. The value is null when
    /// nothing changed since the last checkpoint, so there was nothing to record.
    /// </summary>
    HistoryResult<HistoryEntry?> Checkpoint(CheckpointRequest request, CancellationToken cancellationToken = default);

    /// <summary>Names a revision with a tag, such as the snapshot taken when a book is frozen (design.md §5).</summary>
    HistoryResult<HistoryTag> Tag(TagRequest request, CancellationToken cancellationToken = default);

    /// <summary>The repository's tags whose names start with <paramref name="prefix"/>, or every tag if it's null, in ASCII order of name.</summary>
    HistoryResult<IReadOnlyList<HistoryTag>> Tags(string? prefix = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if the latest recorded state (for git, HEAD) already holds the
    /// den file at <paramref name="path"/> with exactly <paramref name="content"/>,
    /// as the backend would record those bytes; false if it holds something
    /// else, or no such file. The MCP <c>checkpoint</c> uses it to tell a
    /// file it wrote that's been recorded since from one still to record.
    /// A backend without it says it isn't available.
    /// </summary>
    HistoryResult<bool> HoldsAtLatest(string path, byte[] content, CancellationToken cancellationToken = default) =>
        HistoryResult<bool>.Unavailable("This history backend can't compare a file with its latest recorded state.");
}

/// <summary>What a history backend can do for a den.</summary>
[Flags]
public enum HistoryCapabilities
{
    /// <summary>Nothing: the den has no history backend.</summary>
    None = 0,

    /// <summary>List checkpoints (<see cref="IHistoryProvider.Log"/>).</summary>
    Log = 1,

    /// <summary>Compare revisions (<see cref="IHistoryProvider.Diff"/>).</summary>
    Diff = 2,

    /// <summary>Record checkpoints (<see cref="IHistoryProvider.Checkpoint"/>).</summary>
    Checkpoint = 4,

    /// <summary>
    /// Make and list tags (<see cref="IHistoryProvider.Tag"/>, <see cref="IHistoryProvider.Tags"/>).
    /// Freezing a book adds its tag only when the backend has this
    /// (design.md §5): <c>status: final</c> alone is the freeze (F-BOOK-1).
    /// </summary>
    Tag = 8,

    /// <summary>Everything above.</summary>
    All = Log | Diff | Checkpoint | Tag,
}

/// <summary>
/// The outcome of a history operation: its value, or, when the backend
/// can't do it, why not, in words for the writer.
/// </summary>
/// <typeparam name="T">The value of a done operation.</typeparam>
public sealed class HistoryResult<T>
{
    private readonly T _value;

    private HistoryResult(bool available, T value, string? reason)
    {
        IsAvailable = available;
        _value = value;
        Reason = reason;
    }

    /// <summary>True if the operation was done and <see cref="Value"/> holds its result.</summary>
    public bool IsAvailable { get; }

    /// <summary>The result of the operation.</summary>
    /// <exception cref="InvalidOperationException">The operation wasn't available: see <see cref="Reason"/>.</exception>
    public T Value => IsAvailable ? _value : throw new InvalidOperationException(Reason);

    /// <summary>Why the operation wasn't available, for the writer, or null if it was done.</summary>
    public string? Reason { get; }

    /// <summary>A done operation's result.</summary>
    public static HistoryResult<T> Done(T value) => new(true, value, null);

    /// <summary>An operation the backend can't do, and why, in words for the writer.</summary>
    public static HistoryResult<T> Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, default!, reason);
    }
}

/// <summary>What a log asks for, a page at a time.</summary>
/// <param name="Path">A den-relative file or folder to limit the log to, or null for the whole den.</param>
/// <param name="Limit">The most entries to return, or null for all.</param>
/// <param name="Before">
/// Only the entries after this one in the same log, or null to start from
/// the newest. For the next page, pass the <see cref="HistoryEntry.Id"/> of
/// the last entry of the page before, with the same <c>Path</c>. A revision
/// that isn't an entry of this log is refused.
/// </param>
/// <remarks>
/// A history with merges isn't a line, so "older than" means "after it in
/// the same listing", not "its ancestors". For git (#19), each page walks
/// the same log from HEAD with the same path filter, skips the entries up
/// to and including <c>Before</c>, and takes <c>Limit</c> more. Neither
/// <c>git log &lt;before&gt;</c> (with or without <c>--skip=1</c>) nor
/// <c>git log &lt;before&gt;^</c> will do: both list only the cursor's
/// ancestors, so when a page ends on a commit from a merged branch, the
/// main line's older commits that aren't its ancestors are never listed.
/// </remarks>
public sealed record HistoryLogQuery(string? Path = null, int? Limit = null, string? Before = null);

/// <summary>What a diff compares.</summary>
/// <param name="From">The older revision, or null for the latest checkpoint.</param>
/// <param name="To">The newer revision, or null for the den's files as they are now. Both null: what changed since the latest checkpoint.</param>
/// <param name="Path">A den-relative file or folder to limit the diff to, or null for the whole den.</param>
public sealed record HistoryDiffQuery(string? From = null, string? To = null, string? Path = null);

/// <summary>A checkpoint to record.</summary>
/// <param name="Message">What the checkpoint is, in the writer's words ("end of chapter 14 session").</param>
/// <param name="Paths">The den-relative files and folders to record, or null for the whole den.</param>
/// <param name="CoAuthors">
/// Who else made the changes, as <c>Name &lt;email&gt;</c>, such as an AI
/// agent writing through MCP, which design.md §6 records as a co-author.
/// </param>
public sealed record CheckpointRequest(string Message, IReadOnlyList<string>? Paths = null, IReadOnlyList<string>? CoAuthors = null);

/// <summary>A tag to make.</summary>
/// <param name="Name">The tag's name.</param>
/// <param name="At">The revision to tag, or null for the latest checkpoint.</param>
/// <param name="Message">A message for the tag, or null for none.</param>
public sealed record TagRequest(string Name, string? At = null, string? Message = null);

/// <summary>One checkpoint in a den's history.</summary>
/// <param name="Id">The backend's id for it (a commit hash, for git).</param>
/// <param name="When">When it was recorded.</param>
/// <param name="Author">Who recorded it.</param>
/// <param name="Message">Its message.</param>
/// <param name="Changes">The den files it changed. Files outside the den are left out.</param>
public sealed record HistoryEntry(string Id, DateTimeOffset When, string Author, string Message, IReadOnlyList<HistoryFileChange> Changes);

/// <summary>How a checkpoint or diff changed one file.</summary>
public enum HistoryChangeKind
{
    /// <summary>The file is new.</summary>
    Added,

    /// <summary>The file's contents changed.</summary>
    Modified,

    /// <summary>The file was deleted.</summary>
    Deleted,

    /// <summary>The file was moved or renamed from <see cref="HistoryFileChange.OldPath"/>, and may have changed too.</summary>
    Renamed,
}

/// <summary>A change to one den file.</summary>
/// <param name="Kind">How it changed.</param>
/// <param name="Path">Its den-relative path, after the change.</param>
/// <param name="OldPath">Its path before a rename, or null.</param>
public sealed record HistoryFileChange(HistoryChangeKind Kind, string Path, string? OldPath = null);

/// <summary>What changed between two revisions.</summary>
/// <param name="From">The older revision: the one asked for, or the latest checkpoint's id.</param>
/// <param name="To">The newer revision, as asked, or null for the files as they are now.</param>
/// <param name="Files">Each den file that differs, with its unified diff.</param>
public sealed record HistoryDiff(string From, string? To, IReadOnlyList<HistoryFileDiff> Files);

/// <summary>How one file differs.</summary>
/// <param name="Change">How the file changed.</param>
/// <param name="Patch">The change as a unified diff, or empty when only the name changed.</param>
public sealed record HistoryFileDiff(HistoryFileChange Change, string Patch);

/// <summary>A named revision.</summary>
/// <param name="Name">The tag's name.</param>
/// <param name="Id">The revision it names.</param>
public sealed record HistoryTag(string Name, string Id);

/// <summary>A history backend that can do an operation but failed while doing it, such as a git command that exited with an error.</summary>
public class HistoryException : IOException
{
    /// <summary>A failure, said for the writer in <paramref name="message"/>.</summary>
    public HistoryException(string message)
        : base(message)
    {
    }

    /// <summary>A failure, said for the writer in <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public HistoryException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A revision the caller gave isn't one the history has: it doesn't name a
/// checkpoint, or, as a log's <see cref="HistoryLogQuery.Before"/>, it isn't
/// an entry of that log. The caller's mistake, not the backend's failure.
/// </summary>
public sealed class HistoryRevisionException : HistoryException
{
    /// <summary>A revision that isn't one, said for the writer in <paramref name="message"/>.</summary>
    public HistoryRevisionException(string revision, string message)
        : base(message)
    {
        Revision = revision;
    }

    /// <summary>The revision as the caller gave it.</summary>
    public string Revision { get; }
}
