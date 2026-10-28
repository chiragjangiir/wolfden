using Wolfden.Core.History;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;
using Wolfden.Index.Reading;

namespace Wolfden.Index.Writing;

/// <summary>
/// Freezing and unfreezing a book (design.md §5, format.md F-BOOK-1, #31):
/// for the writer, from the web UI and the CLI. There are no MCP tools for
/// them (maintainer decision, 2026-10-28): an agent's <c>frozen</c> error
/// tells it to ask the writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Freezing</b> takes three steps, in this order (maintainer decision,
/// 2026-10-25): <c>status: final</c> in <c>book.md</c>, edited in place
/// like any field write; then, when the den has history, a checkpoint with
/// that change; then a tag on that checkpoint (<see cref="FreezeTag"/>),
/// so a tag never names a snapshot where the book is still a draft.
/// Without history, the first step alone is the freeze. If the checkpoint
/// fails, the book stays final with no commit or tag; if the tag fails, the
/// commit stands. Either way, freezing again picks up where it stopped. The
/// checkpoint records the whole den, as every checkpoint does by default
/// (design choice, #31), and the result counts the files it took in from
/// outside the book, without naming them.
/// </para>
/// <para>
/// <b>Done, or a step missing</b> (design choice, #31) is decided by the
/// book, not by <c>book.md</c> alone, so a book marked final by hand, then
/// edited, is snapshotted as it is now. When anything in the book's folder
/// (its <c>book.md</c>, outline, and notes) differs from the latest
/// checkpoint, freezing checkpoints. Then it looks at the latest checkpoint
/// that changed the book's folder, which holds the book as it is now: if a
/// freeze tag of the book names that checkpoint or a later one, the freeze
/// is done; otherwise it tags that checkpoint. A tag from an earlier freeze
/// doesn't count, so a book frozen again gets its own tag.
/// After a tag that failed, that's the freeze's own checkpoint, even if
/// other work was checkpointed since.
/// </para>
/// <para>
/// <b>Unfreezing</b> writes <c>status: draft</c> (not removing the line),
/// then checkpoints when the den has history, with no tag (maintainer
/// decision, 2026-10-28). If the checkpoint fails, the book stays a draft
/// and the result says so.
/// </para>
/// </remarks>
public sealed partial class DenTools
{
    /// <summary>
    /// A book's freeze status, for a page that offers to freeze or unfreeze
    /// it: its status, the hash of its <c>book.md</c> to pass back, and what
    /// a freeze would add to the den's history.
    /// </summary>
    public BookFreezeView GetBookFreeze(string book, string? universe = null)
    {
        var (universeKey, key) = BookArgument(book, universe);
        var target = FindBook(DenStructure.Load(Snapshot()), universeKey, key);
        var history = History();
        var canCheckpoint = history.CheckpointUnavailable is null;
        var canTag = canCheckpoint && history.Capabilities.HasFlag(HistoryCapabilities.Tag);
        var bytes = target.File.ToBytes();
        string? nextTag = null;
        var unfinished = false;
        var others = 0;
        try
        {
            if (canTag && target.Status != BookStatus.Final)
            {
                nextTag = FreezeTag.NextName(history, universeKey, target.Key);
            }

            if (canCheckpoint)
            {
                var pending = PendingChanges(history);
                others = pending?.Count(c => !InFolder(c, target.Path)) ?? 0;
                unfinished = target.Status == BookStatus.Final && FreezeTarget(history, universeKey, target, bytes, pending) is not { Done: true };
            }
        }
        catch (HistoryException)
        {
            // Only hints for the page: a freeze itself says what went wrong.
        }

        return new BookFreezeView(
            DenReader.ResultVersion, universeKey, target.Key, target.Name, target.FilePath, HashOf(bytes), StatusWord(target.Status),
            history.Name, canCheckpoint, canCheckpoint ? null : history.CheckpointUnavailable, nextTag, unfinished, others);
    }

    /// <summary>
    /// Freezes a book: <c>status: final</c> in its <c>book.md</c>, then, when
    /// the den has history, a checkpoint and a freeze tag. A book that's
    /// already final is checkpointed and tagged if that hasn't been done.
    /// </summary>
    /// <param name="book">The book's key.</param>
    /// <param name="expectedHash">The hash of the <c>book.md</c> the writer saw, or null to take it as it is now.</param>
    /// <param name="universe">The book's universe, when the den has several.</param>
    public FreezeResult FreezeBook(string book, string? expectedHash, string? universe = null)
    {
        var (universeKey, key) = BookArgument(book, universe);
        lock (_writeLock)
        {
            var (target, bytes, changed, idAdded, warnings) = SetStatus(universeKey, key, expectedHash, BookStatus.Final);
            var said = $"Book '{target.Name}'";
            var history = History();
            HistoryEntry? made = null;
            var others = 0;
            FreezeResult Frozen(string snapshot, string? checkpoint, string? tag, string message) => new(
                DenReader.ResultVersion, universeKey, target.Key, target.FilePath, HashOf(bytes), "final", changed, history.Name, snapshot,
                checkpoint ?? made?.Id, tag, message + Others(others), warnings, idAdded, others);

            if (history.CheckpointUnavailable is { } why)
            {
                return Frozen(FreezeSnapshot.NoHistory, null, null, changed
                    ? $"Froze book '{target.Name}': book.md now says status: final. No snapshot was taken: {why}"
                    : $"{said} is already frozen (status: final). No snapshot was taken: {why}");
            }

            // Step 2: a checkpoint, unless the latest one already holds the book as it is.
            try
            {
                if (changed || !BookHeld(history, target, bytes, PendingChanges(history)))
                {
                    made = SessionWritten.Checkpoint(history, new CheckpointRequest($"Freeze book '{target.Key}' of universe '{universeKey}'")).Result is { IsAvailable: true } r
                        ? r.Value
                        : null;
                    others = made?.Changes.Count(c => !InFolder(c, target.Path)) ?? 0;
                }
            }
            catch (HistoryException e)
            {
                return Frozen(FreezeSnapshot.CheckpointFailed, null, null,
                    $"{said} is final in book.md{(changed ? " now" : "")}, but the checkpoint failed, so nothing was committed or tagged: {e.Message} " +
                    "Once that's fixed, freeze the book again to take the snapshot.");
            }

            // Step 3: tag the checkpoint that holds the book as it is, unless a freeze tag already holds it.
            string? at = made?.Id;
            try
            {
                if (FreezeTarget(history, universeKey, target, bytes, PendingChanges(history)) is not { } found)
                {
                    // The checkpoint recorded nothing of the book (git ignores book.md, say), so no checkpoint
                    // holds it final: a tag now could name one where it's a draft.
                    return Frozen(FreezeSnapshot.NotTagged, null, null,
                        $"{said} is final in book.md, but no checkpoint holds the book as it is now{(made is null ? "" : $" (git recorded {Short(made.Id)} without it)")}, " +
                        "so no tag was added: git may be ignoring some of its files. Check 'git status', then freeze the book again.");
                }

                at = found.At;
                if (found.Tag is { } tagged)
                {
                    return Frozen(FreezeSnapshot.AlreadyTagged, at, tagged.Name, changed
                        ? $"Froze book '{target.Name}' again: book.md says status: final, and the snapshot tagged '{tagged.Name}' already holds the book as it is, so no tag was added.{Checkpointed(made, sentence: true)}"
                        : $"{said} is already frozen, and its snapshot is tagged '{tagged.Name}'. Nothing changed.");
                }

                var tag = FreezeTag.Add(history, universeKey, target.Key, at);
                if (tag.Outcome != FreezeTagOutcome.Tagged)
                {
                    return Frozen(FreezeSnapshot.NotTagged, at, null,
                        $"{said} is final{Checkpointed(made)}, but no tag was added: {tag.Message}");
                }

                return Frozen(FreezeSnapshot.Tagged, at, tag.Name, changed
                    ? $"Froze book '{target.Name}': book.md now says status: final.{Checkpointed(made, sentence: true)} Tagged the snapshot '{tag.Name}'."
                    : $"{said} was already final.{Checkpointed(made, sentence: true)} Tagged its snapshot '{tag.Name}'.");
            }
            catch (HistoryException e)
            {
                return Frozen(FreezeSnapshot.TagFailed, at, null,
                    $"{said} is final{(at is null ? "" : $" and checkpointed ({Short(at)})")}, but the tag failed: {e.Message} " +
                    "The checkpoint stands. Once that's fixed, freeze the book again to add the tag.");
            }
        }
    }

    /// <summary>Where a final book's freeze tag goes: the checkpoint that holds the book as it is, and the freeze tag already holding it, if any.</summary>
    /// <param name="At">The latest checkpoint that changed the book's folder.</param>
    /// <param name="Tag">A freeze tag of the book that holds the book's folder as <paramref name="At"/> does, or null.</param>
    /// <param name="Done">True if the freeze is done: a tag holds the book, or the history can't make tags.</param>
    private sealed record FreezeTargetResult(string At, HistoryTag? Tag, bool Done);

    /// <summary>
    /// Where a final book's freeze tag goes, or null if no checkpoint holds
    /// the book as it is now (something in its folder isn't checkpointed).
    /// </summary>
    private static FreezeTargetResult? FreezeTarget(
        IHistoryProvider history, string universeKey, Book target, byte[] bytes, IReadOnlyList<HistoryFileChange>? pending)
    {
        if (!BookHeld(history, target, bytes, pending))
        {
            return null;
        }

        var log = history.Log(new HistoryLogQuery(target.Path, Limit: 1));
        if (!log.IsAvailable || log.Value.Count == 0)
        {
            return null;
        }

        var at = log.Value[0].Id;
        if (!history.Capabilities.HasFlag(HistoryCapabilities.Tag))
        {
            return new FreezeTargetResult(at, null, true);
        }

        // A tag at that checkpoint, or at a later one, which holds the book's folder the same since
        // nothing changed it after. A tag from before it (an earlier freeze) doesn't count, even if
        // the book reads the same, so a book frozen again gets its own tag (maintainer decision, 2026-10-28).
        var tags = FreezeTag.Existing(history, universeKey, target.Key);
        var tag = tags.LastOrDefault(t => t.Id == at);
        if (tag is null && tags.Count > 0)
        {
            var later = CheckpointsAfter(history, at);
            tag = tags.LastOrDefault(t => later.Contains(t.Id));
        }

        return new FreezeTargetResult(at, tag, tag is not null);
    }

    /// <summary>The checkpoints listed before <paramref name="at"/> in the den's log from the latest: those made after it.</summary>
    private static HashSet<string> CheckpointsAfter(IHistoryProvider history, string at)
    {
        var after = new HashSet<string>(StringComparer.Ordinal);
        string? before = null;
        while (true)
        {
            var page = history.Log(new HistoryLogQuery(Limit: 100, Before: before));
            if (!page.IsAvailable || page.Value.Count == 0)
            {
                return after;
            }

            foreach (var entry in page.Value)
            {
                if (entry.Id == at)
                {
                    return after;
                }

                after.Add(entry.Id);
            }

            before = page.Value[^1].Id;
        }
    }

    /// <summary>
    /// True if the latest checkpoint holds the book as it is now: its
    /// <c>book.md</c> as <paramref name="bytes"/>, and nothing in its folder
    /// changed since (<paramref name="pending"/>; null if the history couldn't say, which counts as changed).
    /// </summary>
    private static bool BookHeld(IHistoryProvider history, Book target, byte[] bytes, IReadOnlyList<HistoryFileChange>? pending) =>
        HoldsAtLatest(history, target.FilePath, bytes) && pending is not null && !pending.Any(c => InFolder(c, target.Path));

    /// <summary>What a checkpoint now would record, or null if the history can't say.</summary>
    private static IReadOnlyList<HistoryFileChange>? PendingChanges(IHistoryProvider history) =>
        history.Pending() is { IsAvailable: true } p ? p.Value : null;

    /// <summary>True if the change is to a file in <paramref name="folder"/>, on either side of a rename.</summary>
    private static bool InFolder(HistoryFileChange change, string folder) =>
        IsAtOrUnder(change.Path, folder) || (change.OldPath is { } old && IsAtOrUnder(old, folder));

    /// <summary>
    /// Unfreezes a book: <c>status: draft</c> in its <c>book.md</c>, then,
    /// when the den has history, a checkpoint, with no tag. A book that
    /// isn't final is left as it is.
    /// </summary>
    /// <param name="book">The book's key.</param>
    /// <param name="expectedHash">The hash of the <c>book.md</c> the writer saw, or null to take it as it is now.</param>
    /// <param name="universe">The book's universe, when the den has several.</param>
    public FreezeResult UnfreezeBook(string book, string? expectedHash, string? universe = null)
    {
        var (universeKey, key) = BookArgument(book, universe);
        lock (_writeLock)
        {
            var (target, bytes, changed, idAdded, warnings) = SetStatus(universeKey, key, expectedHash, BookStatus.Draft);
            var said = $"Book '{target.Name}'";
            var history = History();
            var others = 0;
            FreezeResult Unfrozen(string snapshot, string? checkpoint, string message) => new(
                DenReader.ResultVersion, universeKey, target.Key, target.FilePath, HashOf(bytes), StatusWord(changed ? BookStatus.Draft : target.Status), changed,
                history.Name, snapshot, checkpoint, null, message + Others(others), warnings, idAdded, others);

            if (!changed)
            {
                return Unfrozen(FreezeSnapshot.NotFrozen, null, $"{said} isn't frozen, so nothing changed.");
            }

            var done = $"Unfroze book '{target.Name}': book.md now says status: draft, so the book can be edited again.";
            if (history.CheckpointUnavailable is { } why)
            {
                return Unfrozen(FreezeSnapshot.NoHistory, null, $"{done} No checkpoint was made: {why}");
            }

            try
            {
                var made = SessionWritten.Checkpoint(history, new CheckpointRequest($"Unfreeze book '{target.Key}' of universe '{universeKey}'")).Result;
                if (made is { IsAvailable: true, Value: { } entry })
                {
                    others = entry.Changes.Count(c => !InFolder(c, target.Path));
                    return Unfrozen(FreezeSnapshot.Checkpointed, entry.Id, $"{done} Checkpointed the den ({Short(entry.Id)}).");
                }

                return Unfrozen(FreezeSnapshot.NotCheckpointed, null, $"{done} No checkpoint was made: {made.Reason ?? "there was nothing to record."}");
            }
            catch (HistoryException e)
            {
                return Unfrozen(FreezeSnapshot.CheckpointFailed, null,
                    $"{done} But the checkpoint failed, so the change isn't in the den's history yet: {e.Message} Make a checkpoint once that's fixed.");
            }
        }
    }

    /// <summary>The universe and key of the book named <paramref name="book"/>, after checking that it's there.</summary>
    private (string Universe, string Key) BookArgument(string book, string? universe)
    {
        if (string.IsNullOrWhiteSpace(book))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the book's key, such as 'dracula'.");
        }

        var key = book.Trim();
        var (universeKey, _) = LocateBook(key, universe);
        return (universeKey, key);
    }

    /// <summary>
    /// Sets the book's <c>status</c> to <paramref name="status"/> in its
    /// <c>book.md</c>, in place, unless it already says so. Called under the
    /// write lock.
    /// </summary>
    private (Book Book, byte[] Bytes, bool Changed, string? IdAdded, IReadOnlyList<DiagnosticView> Warnings) SetStatus(
        string universeKey, string key, string? expectedHash, BookStatus status)
    {
        var den = Snapshot();
        var structure = DenStructure.Load(den);
        var target = FindBook(structure, universeKey, key);
        var path = target.FilePath;
        var original = target.File.ToBytes();
        if (expectedHash is not null)
        {
            CheckHash(path, original, RequireHash(expectedHash));
        }

        if (target.Status == status)
        {
            return (target, original, false, null, []);
        }

        // Written out, never removed: 'status: draft' says the book was unfrozen (maintainer decision, 2026-10-28).
        var file = Apply(() => target.File.WithFrontmatterValue(["status"], YamlScalarValue.String(StatusWord(status))));
        var idAdded = target.Id is null ? AddId(ref file) : null;
        var bytes = file.ToBytes();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = bytes };
        var check = Check(den, structure, files);
        Save(files, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = original }, created: null);
        return (target, bytes, true, idAdded, Warnings(check));
    }

    /// <summary>True if the latest checkpoint holds the file as <paramref name="bytes"/>; false if not, or if the backend can't say.</summary>
    private static bool HoldsAtLatest(IHistoryProvider history, string path, byte[] bytes) =>
        history.HoldsAtLatest(path, bytes) is { IsAvailable: true, Value: true };

    private static string Checkpointed(HistoryEntry? made, bool sentence = false) =>
        made is null ? "" : sentence ? $" Checkpointed the den ({Short(made.Id)})." : $" and checkpointed ({Short(made.Id)})";

    /// <summary>What the checkpoint took in from outside the book, counted, never named (a pinned server mustn't name other universes' files).</summary>
    private static string Others(int others) => others switch
    {
        0 => "",
        1 => " The checkpoint also recorded 1 other changed file outside the book.",
        _ => $" The checkpoint also recorded {others} other changed files outside the book.",
    };

    private static string Short(string id) => id.Length > 7 ? id[..7] : id;

    private static string StatusWord(BookStatus status) => status == BookStatus.Final ? "final" : "draft";
}

/// <summary>What a freeze or unfreeze did in the den's history (<see cref="FreezeResult.Snapshot"/>).</summary>
public static class FreezeSnapshot
{
    /// <summary>Freeze: the snapshot was tagged (after a checkpoint, if one was needed).</summary>
    public const string Tagged = "tagged";

    /// <summary>Freeze: a freeze tag already holds the book as it is, so no tag was added.</summary>
    public const string AlreadyTagged = "already_tagged";

    /// <summary>The den has no history, so no snapshot was taken; the book's status still changed.</summary>
    public const string NoHistory = "no_history";

    /// <summary>The checkpoint failed: the status change stands, with no commit (and, for a freeze, no tag). Freezing again resumes there.</summary>
    public const string CheckpointFailed = "checkpoint_failed";

    /// <summary>Freeze: the checkpoint stands, but the tag failed. Freezing again adds it.</summary>
    public const string TagFailed = "tag_failed";

    /// <summary>Freeze: the history can't make tags, or no checkpoint holds the book as it is, so no tag was added.</summary>
    public const string NotTagged = "not_tagged";

    /// <summary>Unfreeze: the change was checkpointed.</summary>
    public const string Checkpointed = "checkpointed";

    /// <summary>Unfreeze: the history made no checkpoint, without failing.</summary>
    public const string NotCheckpointed = "not_checkpointed";

    /// <summary>Unfreeze: the book wasn't final, so nothing was done.</summary>
    public const string NotFrozen = "not_frozen";
}
