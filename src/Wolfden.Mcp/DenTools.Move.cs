using Wolfden.Core.Documents;
using Wolfden.Core.Editing;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Mcp;

/// <summary>
/// Moving an entity's file to another scope of its universe (#89): the
/// universe, a series, or a book. Canon views stay as they were: an entity
/// with no <c>from</c> gets the entry point it had as an explicit
/// <c>from</c>, and a link to it that would stop reaching it is rewritten to
/// its id (format.md F-RT-5, maintainer decision 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// When the reader meets the entity outside the new scope (a series-level
/// character first met in Book 1, moved into Book 2), the move is refused
/// (<see cref="ToolErrorCodes.EntryOutsideScope"/>), since MCP can't ask
/// the writer. The error names both ways forward: <c>keep_entry</c> keeps
/// the old entry point as <c>from</c> (a book entity whose <c>from</c> is
/// outside its book gets the F-ENT-6 warning), and <c>enter_at_new_scope</c>
/// has it enter at the start of the new scope.
/// </para>
/// <para>
/// Scope breaks ties between links (F-LINK-7), so a move can also change
/// what other links reach. As for renames (#156), a move that would change
/// which entity a link reaches is refused unless <c>allow_relinks</c>, and
/// any other link that resolves differently is listed in the effects.
/// </para>
/// </remarks>
public sealed partial class DenTools
{
    /// <summary>
    /// Moves an entity's file into <paramref name="toSeries"/>, into
    /// <paramref name="toBook"/>, or, with neither, into its universe itself,
    /// keeping its place under <c>entities/</c>.
    /// </summary>
    public WriteResult MoveEntity(
        string entity, string expectedHash, string? toSeries = null, string? toBook = null, bool keepEntry = false, bool enterAtNewScope = false,
        string? universe = null, bool allowRelinks = false)
    {
        var hash = RequireHash(expectedHash);
        if (toSeries is not null && toBook is not null)
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument, "Give 'to_series' or 'to_book', not both. Leave both out to move the entity to the universe itself.");
        }

        if (keepEntry && enterAtNewScope)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give keep_entry or enter_at_new_scope, not both: they're the two ways forward when the entity enters canon outside the new scope.");
        }

        var (path, ids) = Locate(entity, universe);
        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var catalog = EntityCatalog.Load(structure, den);
            var target = catalog.FindByPath(path) ?? throw (den.FileExists(path)
                ? new DenToolException(
                    ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be moved here. {ToolWords.Cap(Words.CheckFile(path))} to see them.")
                : Gone(path));
            var original = target.File.ToBytes();
            CheckHash(path, original, hash);
            CheckNotFrozen(target);
            var scope = Destination(target.Universe, toSeries, toBook);
            if (scope == target.Scope)
            {
                return new WriteResult(
                    ToolJson.Version, false, path, HashOf(original), Summary(target), [$"Nothing changed: '{target.Name}' is filed in {EntityMove.Describe(scope)} already."], [], []);
            }

            if (scope is Book { Status: BookStatus.Final } frozen)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen, $"Book '{frozen.Key}' is final (frozen), so nothing can be moved into it until it's unfrozen (F-BOOK-1).");
            }

            var to = EntityMove.Destination(target, scope);
            var slash = to.LastIndexOf('/');
            if (den.GetFiles(to[..slash]).FirstOrDefault(n => string.Equals(n, to[(slash + 1)..], StringComparison.OrdinalIgnoreCase)) is { } taken)
            {
                throw new DenToolException(
                    ToolErrorCodes.AlreadyExists,
                    $"{to[..slash]}/{taken} is already there, so '{target.Name}' can't move to {to}. Nothing was changed. Rename one of the files first, or move it somewhere else.");
            }

            // Where the entity enters canon, kept or changed as the caller chose (F-ENT-6, F-RT-5).
            var entry = Entry(target, scope, keepEntry, enterAtNewScope);

            // A missing id is added in the same change, so links can follow by id (F-RT-5, F-ID-6).
            string? idAdded = null;
            if (target.Id is null)
            {
                var file = target.File;
                idAdded = AddId(ref file, ids) is { } added ? added : null;
            }

            var id = target.Id ?? idAdded;
            MarkdownFile WithIdAdded(MarkdownFile file) => idAdded is null ? file : Apply(() => file.WithId(idAdded));

            var links = LinkIndex.Build(catalog, den);
            EntityMovePlan plan;
            try
            {
                plan = EntityMove.Plan(
                    links, den, target, scope, id, f => WithIdAdded(entry.Edit(f)), entry.InterimEdit is { } interim ? f => WithIdAdded(interim(f)) : null,
                    rewriteOwnLinks: !allowRelinks);
            }
            catch (InvalidOperationException e)
            {
                throw new DenToolException(ToolErrorCodes.UnsupportedEdit, $"{e.Message} Give it an id by hand first, or link those by id or another unique form.");
            }
            catch (Exception e) when (e is NotSupportedException or EditVerificationException)
            {
                throw new DenToolException(ToolErrorCodes.UnsupportedEdit, $"The move can't be made in place: {e.Message}");
            }

            if (plan.FrozenPaths.Count > 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen,
                    $"Links to '{target.Name}' in files of a final (frozen) book would stop reaching it once it's moved (F-LINK-7), and they can't be rewritten to its id " +
                    $"until the book is unfrozen (F-BOOK-1): {string.Join(", ", plan.FrozenPaths)}. Nothing was changed.");
            }

            var files = plan.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var check = Check(den, structure, files);

            // The file is rewritten in place before it moves, so that state must load too.
            Check(den, structure, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = plan.Interim });

            // A link to it that still wouldn't reach it, even by id (another file has the same id, say).
            var lost = plan.Changes.Where(c => c.Before.Target?.Path == path).ToList();
            if (lost.Count > 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"{lost.Count} link(s) to '{target.Name}' wouldn't reach it after the move, even by its id: " +
                    $"{string.Join("; ", lost.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} would {Outcome(l.After)}"))}. Nothing was changed. {ToolWords.Cap(Words.CheckDen)} to see why.");
            }

            // Links that reached another entity and wouldn't any more, or would reach another
            // (a nearer scope wins a tie, F-LINK-7), refuse the move unless allow_relinks says
            // to go ahead (maintainer decision, 2026-10-26). That includes the moved file's own
            // links whose target has no id to rewrite them to. The moved file's other own links
            // were rewritten to their targets' ids by default (maintainer decision on #164), or,
            // with allow_relinks, left to follow the new scope and listed below. A link that was
            // ambiguous or broken is only reported.
            var relinked = plan.Changes.Where(l => l.Before.Outcome == Core.Links.LinkOutcome.Resolved).ToList();
            if (relinked.Count > 0 && !allowRelinks)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"{relinked.Count} link(s) would change which entity they reach once '{target.Name}' is in {EntityMove.Describe(scope)}: " +
                    $"{string.Join("; ", relinked.Take(ListedLinkChanges).Select(l => $"{Where(l.After)}{(InFinalBook(l.Before) ? " (in a final book)" : "")} reaches {Reached(l.Before)} and would {Outcome(l.After)}"))}" +
                    $"{(relinked.Count > ListedLinkChanges ? $"; and {relinked.Count - ListedLinkChanges} more" : "")}. Nothing was changed. " +
                    $"Link those by id or file name first, or {Words.AllowRelinks} to move it anyway.");
            }

            var frozenRelinks = relinked.Where(l => InFinalBook(l.Before)).ToList();
            if (frozenRelinks.Count > 0 && RefuseRelinksInFinalBooks)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen,
                    $"{frozenRelinks.Count} link(s) in a final (frozen) book reach another entity and would change target (F-BOOK-1): " +
                    $"{string.Join("; ", frozenRelinks.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} reaches {Reached(l.Before)}"))}. Nothing was changed.");
            }

            SaveMove(path, plan.To, plan.Interim, files, plan.Originals);
            var after = EntityCatalog.ReadEntityFile(check.Structure, plan.To, files[plan.To]).Entity!;
            var effects = new List<string>
            {
                $"Moved '{target.Name}' from {EntityMove.Describe(target.Scope)} to {EntityMove.Describe(scope)}: {path} is now {plan.To}. " +
                "Its file name is the same, so links by file name, name, alias, or id find it as before, except as said below.",
            };
            effects.AddRange(entry.Effects);
            if (plan.Rewritten.Count > 0)
            {
                var rewrittenFiles = plan.Rewritten.Select(l => l.Occurrence.Path == path ? plan.To : l.Occurrence.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                effects.Add(
                    $"{plan.Rewritten.Count} link(s) to it would have stopped reaching it (another entity in a nearer scope, or a tie, F-LINK-7), so they were rewritten " +
                    $"to its id, showing the same text as before (F-RT-5), in: {string.Join(", ", rewrittenFiles)}.");
            }

            // Its own links that would have reached another entity, or none, from the new scope.
            effects.AddRange(plan.OwnRewritten.Select(l =>
                $"Its own link {l.Occurrence.Link.Text} (line {l.Occurrence.Link.Line}) reached {Reached(l)}, and from {EntityMove.Describe(scope)} it wouldn't " +
                $"(F-LINK-7), so it was rewritten to that entity's id, showing the same text as before. Pass allow_relinks to leave such links to follow the new scope instead."));

            effects.AddRange(plan.Changes.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} {Now(l.After)}; before, it {Was(l.Before)}."));
            if (plan.Changes.Count > ListedLinkChanges)
            {
                effects.Add($"And {plan.Changes.Count - ListedLinkChanges} more link(s) resolve differently now. {ToolWords.Cap(Words.CheckDen)} to see them.");
            }

            // Row ids resolve among the outlines of the entity's series or standalone book, or the whole universe (F-FLD-7).
            static Container Rows(Container c) => c is Book { Series: { } s } ? s : c;
            if (target.File.GetFrontmatterValue("fields") is YamlMapping fieldMap && fieldMap.ContainsKey("row") && Rows(scope) != Rows(target.Scope))
            {
                effects.Add(
                    $"Its 'row' ids now resolve among the outlines {(scope is Universe ? "of every book in the universe" : $"of {EntityMove.Describe(Rows(scope))}")} (F-FLD-7). " +
                    "Check that they still name the rows you mean.");
            }

            if (plan.SkippedPaths.Count > 0)
            {
                effects.Add(
                    $"These files have errors, so the den leaves them out and their links couldn't be checked, but their text has links that may be to it: " +
                    $"{string.Join(", ", plan.SkippedPaths)}. Once they're fixed, check that those links still reach it.");
            }

            if (idAdded is not null)
            {
                effects.Add($"The file had no id, so it was given {idAdded} (F-ID-6).");
            }
            else if (target.Id is null)
            {
                effects.Add("The file has no id, and one couldn't be added in place; run 'wolfden check --fix' to add one (F-ID-6).");
            }

            var pinned = entry.ChangesViews && HoldsPinnedCanon(target)
                ? new List<string> { "'from' changes for the entity that holds pinned canon, so it changes where those facts enter canon views" }
                : [];
            return new WriteResult(
                ToolJson.Version, true, plan.To, HashOf(files[plan.To]), Summary(after), effects, pinned, Warnings(check),
                files.OrderBy(p => p.Key == plan.To ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key == plan.To ? new WrittenFileView(p.Key, HashOf(p.Value), Created: true, MovedFrom: path) : new WrittenFileView(p.Key, HashOf(p.Value)))
                    .ToList(),
                IdAdded: idAdded);
        }
    }

    /// <summary>The scope to move to: the series or book named, or the universe itself.</summary>
    private static Container Destination(Universe universe, string? series, string? book)
    {
        if (series is not null)
        {
            var key = series.Trim();
            var found = universe.Series.Where(s => s.Key == key).ToList();
            return found.Count == 1 ? found[0] : throw new DenToolException(
                ToolErrorCodes.UnknownScope,
                $"Universe '{universe.Key}' has {(found.Count == 0 ? "no" : "more than one")} series '{key}'." +
                (universe.Series.Any() ? $" Its series: {string.Join(", ", universe.Series.Select(s => s.Key).Order(StringComparer.Ordinal))}." : " It has no series."));
        }

        if (book is not null)
        {
            var key = book.Trim();
            return universe.FindBook(key) ?? throw new DenToolException(
                ToolErrorCodes.UnknownScope,
                $"Universe '{universe.Key}' has no book '{key}' (or more than one, F-KEY-5)." +
                (universe.Books.Count > 0 ? $" Its books: {string.Join(", ", universe.Books.Select(b => b.Key).Order(StringComparer.Ordinal))}." : " It has no books."));
        }

        return universe;
    }

    /// <summary>What a move does to where the entity enters canon.</summary>
    /// <param name="Edit">The edit of its <c>from</c>, if any.</param>
    /// <param name="Effects">What that means, for the result.</param>
    /// <param name="ChangesViews">True if canon views change: it enters at the start of the new scope now.</param>
    /// <param name="InterimEdit">
    /// The edit for the file in place, before it moves, when it differs from
    /// <paramref name="Edit"/>: a <c>from</c> that only means the same at the
    /// new scope (one taken out, for <c>enter_at_new_scope</c>) waits for the move.
    /// </param>
    private sealed record EntryChange(Func<MarkdownFile, MarkdownFile> Edit, List<string> Effects, bool ChangesViews, Func<MarkdownFile, MarkdownFile>? InterimEdit = null);

    /// <summary>
    /// Keeps where <paramref name="entity"/> enters canon across a move to
    /// <paramref name="scope"/> (maintainer decision, 2026-10-24, #89): a
    /// missing <c>from</c> is written as the entry point it had, unless the
    /// new scope starts at that very point. When it enters outside the new
    /// scope, the move needs the writer's choice, and is refused without it.
    /// </summary>
    private static EntryChange Entry(CatalogEntity entity, Container scope, bool keepEntry, bool enterAtNewScope)
    {
        var universe = entity.Universe;
        var effects = new List<string>();
        Book? book;
        string written;
        string? toWrite = null;
        if (entity.From is { } from)
        {
            written = $"from: \"{from}\"";
            book = universe.FindBook(from.BookKey);
            if (book is null)
            {
                effects.Add($"Its from: \"{from}\" names no book of the universe now, so it was kept as written; where it enters canon can't be told until it resolves (F-POS-4).");
                return new EntryChange(f => f, effects, false);
            }
        }
        else
        {
            // The first book at or after its start orders the same for every position (F-ORD-3,
            // F-POS-7): no chapter lies between them. None when the story has no book after it.
            var start = entity.Scope.Start;
            book = universe.Books.FirstOrDefault(b => b.Start >= start);
            toWrite = book?.Key;
            written = book is null ? entity.Scope.StartDescription : $"from: \"{book.Key}\"";
            if (scope.Start == start)
            {
                effects.Add($"It has no 'from', and it still enters canon at the same point: {entity.Scope.StartDescription} is {scope.StartDescription} (F-ENT-6).");
                return new EntryChange(f => f, effects, false);
            }
        }

        var place = book is null ? Place.Unnamed : PlaceOf(book, scope);
        Func<MarkdownFile, MarkdownFile> keep = f => toWrite is null ? f : WithFrom(f, toWrite);
        if (place == Place.Inside)
        {
            if (keepEntry || enterAtNewScope)
            {
                effects.Add($"It enters canon inside {EntityMove.Describe(scope)}, so {(keepEntry ? "keep_entry" : "enter_at_new_scope")} wasn't needed and changed nothing.");
            }

            if (toWrite is not null)
            {
                effects.Insert(0, $"It had no 'from', so it entered canon at {entity.Scope.StartDescription}; that's now written as {written}, so canon views don't change (F-RT-5, F-ENT-6).");
            }

            return new EntryChange(keep, effects, false);
        }

        var enters = entity.From is not null ? $"{entity.From} (its 'from')" : $"{entity.Scope.StartDescription} (it has no 'from')";
        if (enterAtNewScope)
        {
            effects.Add(
                $"It now enters canon at {scope.StartDescription} (enter_at_new_scope); before the move it entered at {enters}, " +
                (place is Place.After or Place.Unnamed
                    ? $"so canon views from {scope.StartDescription} up to {enters} now include it, earlier than the reader met it before"
                    : "so canon views between the two points don't include it any more") +
                (entity.From is null ? "." : ", and its 'from' was taken out."));

            // In place, before the move, the file keeps its 'from' (or its lack of one), so views
            // don't change until it moves; the 'from' is taken out once it's there.
            return new EntryChange(f => entity.From is null ? f : Apply(() => f.WithoutFrontmatterValue(["from"])), effects, true, f => f);
        }

        if (book is null)
        {
            throw new DenToolException(
                ToolErrorCodes.EntryOutsideScope,
                $"'{entity.Name}' has no 'from', so it enters canon at {entity.Scope.StartDescription}, and no position can name that point: no book comes at or after it in story order. " +
                $"Nothing was changed. Ask the writer, then call again with enter_at_new_scope: true to have it enter canon at {scope.StartDescription}, or give it a 'from' first with update_fields.");
        }

        var describe = place == Place.Before ? $"before the start of {EntityMove.Describe(scope)}" : $"after {EntityMove.Describe(scope)}";
        if (keepEntry)
        {
            effects.Add(
                $"It still enters canon at {enters} (keep_entry), which is {describe}" +
                (toWrite is null ? "." : $"; that's now written as {written}.") +
                (scope is Book ? " A book entity whose 'from' is outside its book gets a warning (F-ENT-6)." : ""));
            return new EntryChange(keep, effects, false);
        }

        var already = place == Place.Before
            ? $"so the reader has already met it when {EntityMove.Describe(scope)} starts"
            : $"so the reader meets it only after {EntityMove.Describe(scope)}";
        var instead = place == Place.Before
            ? $"so canon views before then leave it out"
            : $"but be warned: it would then appear in canon views from {scope.StartDescription} on, earlier than readers now meet it ({enters})";
        throw new DenToolException(
            ToolErrorCodes.EntryOutsideScope,
            $"'{entity.Name}' enters canon at {enters}, in book '{book.Key}', which is {describe}, {already}. " +
            "Moving it there needs the writer's choice, so nothing was changed. Ask the writer, then call again with keep_entry: true to keep " +
            $"{written} (it still enters canon there{(scope is Book ? ", and check warns that its 'from' is outside its book, F-ENT-6" : "")})" +
            $", or enter_at_new_scope: true to have it enter canon at {scope.StartDescription} instead, {instead}.");
    }

    /// <summary>Where the book an entity enters canon in lies, against a scope it moves to.</summary>
    private enum Place
    {
        Inside,
        Before,
        After,

        /// <summary>No book names the entry point (F-POS-7), so it can't be kept as a <c>from</c>.</summary>
        Unnamed,
    }

    private static Place PlaceOf(Book book, Container scope)
    {
        var inside = scope switch
        {
            Universe => true,
            Series s => book.Series == s,
            _ => book == scope,
        };
        return inside ? Place.Inside : book.Start < scope.Start ? Place.Before : Place.After;
    }

    /// <summary>
    /// Saves a move so that every state the den passes through loads with
    /// one copy of the entity and the same canon views: the file is rewritten
    /// in place first (<paramref name="interim"/>: its id, a kept <c>from</c>,
    /// its own links), then the links to it in other files, then the file is
    /// moved whole, an atomic rename that also refuses a name taken in any
    /// case, and last, if the final text differs, rewritten at its new path.
    /// A failure puts back every step made, newest first, but only where the
    /// file still holds what was written, so another program's save is kept.
    /// </summary>
    private void SaveMove(string from, string to, byte[] interim, Dictionary<string, byte[]> files, IReadOnlyDictionary<string, byte[]> originals)
    {
        foreach (var (path, before) in originals)
        {
            var now = Store.TryReadFile(path);
            if (now is null || !now.AsSpan().SequenceEqual(before))
            {
                throw new DenToolException(
                    ToolErrorCodes.StaleFile, $"{path} changed while the move was being made, so nothing was written. Read it again and retry.");
            }
        }

        bool Holds(string path, byte[] bytes) => Store.TryReadFile(path) is { } now && now.AsSpan().SequenceEqual(bytes);

        // Each step made, with how to put it back: the path to check, what it should hold, and the undo.
        var made = new List<(string Path, byte[] Holds, Action Undo)>();
        var final = files[to];
        try
        {
            var original = originals[from];
            if (!interim.AsSpan().SequenceEqual(original))
            {
                Store.WriteFile(from, interim, DenWriteMode.Replace);
                made.Add((from, interim, () => Store.WriteFile(from, original, DenWriteMode.Replace)));
            }

            foreach (var (path, bytes) in files.Where(p => p.Key != to).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Store.WriteFile(path, bytes, DenWriteMode.Replace);
                var was = originals[path];
                made.Add((path, bytes, () => Store.WriteFile(path, was, DenWriteMode.Replace)));
            }

            Store.MoveFile(from, to);
            made.Add((to, interim, () => Store.MoveFile(to, from)));
            if (!final.AsSpan().SequenceEqual(interim))
            {
                Store.WriteFile(to, final, DenWriteMode.Replace);
                made.Add((to, final, () => Store.WriteFile(to, interim, DenWriteMode.Replace)));
            }

            foreach (var (path, bytes) in files.Where(p => p.Key != to))
            {
                _sessionWritten[path] = bytes;
            }

            _sessionWritten[to] = final;
            _sessionWritten[from] = null;
        }
        catch (Exception e)
        {
            var stuck = new List<(string Path, byte[] Holds)>();
            foreach (var (path, holds, undo) in Enumerable.Reverse(made))
            {
                try
                {
                    if (Holds(path, holds))
                    {
                        undo();
                    }
                    else
                    {
                        stuck.Add((path, holds));
                    }
                }
                catch (Exception u) when (u is IOException or UnauthorizedAccessException)
                {
                    stuck.Add((path, holds));
                }
            }

            // A file put back is as the session found it; one that still holds what a step wrote
            // (the in-place rewrite, a link rewrite, or the final text) is the session's.
            foreach (var (path, holds) in stuck)
            {
                if (Holds(path, holds))
                {
                    _sessionWritten[path] = holds;
                }
            }

            var left = stuck.Select(s => s.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (e is not (IOException or UnauthorizedAccessException))
            {
                if (left.Count > 0)
                {
                    throw new DenToolException(
                        ToolErrorCodes.WriteFailed,
                        $"The move stopped ({e.GetType().Name}: {e.Message}), and some files couldn't be put back, and hold the edit: {string.Join(", ", left)}. Check them, or restore them from history.");
                }

                throw;
            }

            var code = e is DenStoreException { Error: DenStoreError.AlreadyExists or DenStoreError.CaseConflict } ? ToolErrorCodes.AlreadyExists : ToolErrorCodes.WriteFailed;
            var said = e is DenStoreException ? e.Message : $"The move failed: {e.Message}";
            throw new DenToolException(
                code,
                said + (made.Count == 0 ? " Nothing was written."
                    : left.Count == 0 ? $" What was already written was put back as it was, and {from} is where it was, so nothing changed."
                    : $" Some files couldn't be put back, and hold the edit: {string.Join(", ", left)}. Check them, or restore them from history."));
        }
    }

    private static MarkdownFile WithFrom(MarkdownFile file, string bookKey) =>
        Apply(() => file.WithFrontmatterValue(["from"], YamlScalarValue.String(bookKey), inTemplateOrder: true));

    /// <summary>True if any of the entity's field entries or sections is pinned canon (F-PIN-1).</summary>
    private static bool HoldsPinnedCanon(CatalogEntity entity)
    {
        var facts = EntityFacts.For(entity, new Bookkeeping(entity.Universe.Den.BookkeepingSections));
        return facts.Fields.Any(f => f.Entries.Any(e => e.Pinned)) || facts.Sections.Any(s => s.Pinned);
    }
}
