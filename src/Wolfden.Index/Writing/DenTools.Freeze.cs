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
/// like any field write; then, when the den has history, a checkpoint of
/// the whole den with that change; then a tag on that checkpoint
/// (<see cref="FreezeTag"/>), so a tag never names a snapshot where the
/// book is still a draft. Without history, the first step alone is the
/// freeze. If the checkpoint fails, the book stays final with no commit or
/// tag; if the tag fails, the commit stands. Either way, freezing again
/// picks up where it stopped.
/// </para>
/// <para>
/// <b>Picking up where it stopped</b> (design choice, #31). Freezing a
/// book that's already final changes nothing in <c>book.md</c>, and goes
/// by its history: if the latest checkpoint doesn't hold <c>book.md</c> as
/// it is now, it checkpoints and tags, as a first freeze would; if it does,
/// it looks at the latest checkpoint that changed <c>book.md</c>, which is
/// the one that made it final, and tags that one unless one of the book's
/// freeze tags already names it. So a freeze is only done once
/// <c>book.md</c> is checkpointed and that checkpoint is tagged.
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
        try
        {
            if (canTag && target.Status != BookStatus.Final)
            {
                nextTag = FreezeTag.NextName(history, universeKey, target.Key);
            }

            unfinished = canCheckpoint && target.Status == BookStatus.Final && !FreezeFinished(history, universeKey, target, bytes);
        }
        catch (HistoryException)
        {
            // Only hints for the page: a freeze itself says what went wrong.
        }

        return new BookFreezeView(
            DenReader.ResultVersion, universeKey, target.Key, target.Name, target.FilePath, HashOf(bytes), StatusWord(target.Status),
            history.Name, canCheckpoint, canCheckpoint ? null : history.CheckpointUnavailable, nextTag, unfinished);
    }

    /// <summary>
    /// True if a final book's freeze is finished: the latest checkpoint
    /// holds its <c>book.md</c> as <paramref name="bytes"/>, and, if the
    /// history can make tags, one of the book's freeze tags names the
    /// latest checkpoint that changed it. Freezing again finishes one that isn't.
    /// </summary>
    private static bool FreezeFinished(IHistoryProvider history, string universeKey, Book target, byte[] bytes)
    {
        if (!HoldsAtLatest(history, target.FilePath, bytes))
        {
            return false;
        }

        if (!history.Capabilities.HasFlag(HistoryCapabilities.Tag))
        {
            return true;
        }

        var log = history.Log(new HistoryLogQuery(target.FilePath, Limit: 1));
        return log.IsAvailable && log.Value.Count > 0 && FreezeTag.Existing(history, universeKey, target.Key).Any(t => t.Id == log.Value[0].Id);
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
            FreezeResult Frozen(string snapshot, string? checkpoint, string? tag, string message) => new(
                DenReader.ResultVersion, universeKey, target.Key, target.FilePath, HashOf(bytes), "final", changed, history.Name, snapshot,
                checkpoint ?? made?.Id, tag, message, warnings, idAdded);

            if (history.CheckpointUnavailable is { } why)
            {
                return Frozen(FreezeSnapshot.NoHistory, null, null, changed
                    ? $"Froze book '{target.Name}': book.md now says status: final. No snapshot was taken: {why}"
                    : $"{said} is already frozen (status: final). No snapshot was taken: {why}");
            }

            // Step 2: a checkpoint, unless the latest one already holds book.md as it is.
            try
            {
                if (changed || !HoldsAtLatest(history, target.FilePath, bytes))
                {
                    made = SessionWritten.Checkpoint(history, new CheckpointRequest($"Freeze book '{target.Key}' of universe '{universeKey}'")).Result is { IsAvailable: true } r
                        ? r.Value
                        : null;
                }
            }
            catch (HistoryException e)
            {
                return Frozen(FreezeSnapshot.CheckpointFailed, null, null,
                    $"{said} is final in book.md{(changed ? " now" : "")}, but the checkpoint failed, so nothing was committed or tagged: {e.Message} " +
                    "Once that's fixed, freeze the book again to take the snapshot.");
            }

            // Step 3: tag the checkpoint that made book.md final, unless it's tagged already.
            string? at = made?.Id;
            try
            {
                var log = history.Log(new HistoryLogQuery(target.FilePath, Limit: 1));
                if (log.IsAvailable && log.Value.Count > 0)
                {
                    at = log.Value[0].Id;
                }

                if (at is null)
                {
                    return Frozen(FreezeSnapshot.NotTagged, null, null,
                        $"{said} is final in book.md, but book.md isn't in any checkpoint, so there's nothing to tag. Make a checkpoint, then freeze the book again to tag it.");
                }

                if (made is null && FreezeTag.Existing(history, universeKey, target.Key).FirstOrDefault(t => t.Id == at) is { } tagged)
                {
                    return Frozen(FreezeSnapshot.AlreadyTagged, at, tagged.Name,
                        $"{said} is already frozen, and its snapshot is tagged '{tagged.Name}'. Nothing changed.");
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
            FreezeResult Unfrozen(string snapshot, string? checkpoint, string message) => new(
                DenReader.ResultVersion, universeKey, target.Key, target.FilePath, HashOf(bytes), StatusWord(changed ? BookStatus.Draft : target.Status), changed,
                history.Name, snapshot, checkpoint, null, message, warnings, idAdded);

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
                return made is { IsAvailable: true, Value: { } entry }
                    ? Unfrozen(FreezeSnapshot.Checkpointed, entry.Id, $"{done} Checkpointed the den ({Short(entry.Id)}).")
                    : Unfrozen(FreezeSnapshot.NotCheckpointed, null, $"{done} No checkpoint was made: {made.Reason ?? "there was nothing to record."}");
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

    private static string Short(string id) => id.Length > 7 ? id[..7] : id;

    private static string StatusWord(BookStatus status) => status == BookStatus.Final ? "final" : "draft";
}

/// <summary>What a freeze or unfreeze did in the den's history (<see cref="FreezeResult.Snapshot"/>).</summary>
public static class FreezeSnapshot
{
    /// <summary>Freeze: the snapshot was tagged (after a checkpoint, if one was needed).</summary>
    public const string Tagged = "tagged";

    /// <summary>Freeze: the book was already final, checkpointed, and tagged, so nothing was done.</summary>
    public const string AlreadyTagged = "already_tagged";

    /// <summary>The den has no history, so no snapshot was taken; the book's status still changed.</summary>
    public const string NoHistory = "no_history";

    /// <summary>The checkpoint failed: the status change stands, with no commit (and, for a freeze, no tag). Freezing again resumes there.</summary>
    public const string CheckpointFailed = "checkpoint_failed";

    /// <summary>Freeze: the checkpoint stands, but the tag failed. Freezing again adds it.</summary>
    public const string TagFailed = "tag_failed";

    /// <summary>Freeze: the history can't make tags, or had nothing to tag, so no tag was added.</summary>
    public const string NotTagged = "not_tagged";

    /// <summary>Unfreeze: the change was checkpointed.</summary>
    public const string Checkpointed = "checkpointed";

    /// <summary>Unfreeze: the history made no checkpoint, without failing.</summary>
    public const string NotCheckpointed = "not_checkpointed";

    /// <summary>Unfreeze: the book wasn't final, so nothing was done.</summary>
    public const string NotFrozen = "not_frozen";
}
