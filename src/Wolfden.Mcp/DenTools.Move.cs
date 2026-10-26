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
                    ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be moved here. Call check with path '{path}' to see them.")
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
            MarkdownFile EditOwn(MarkdownFile file)
            {
                file = entry.Edit(file);
                return idAdded is null ? file : Apply(() => file.WithId(idAdded));
            }

            var links = LinkIndex.Build(catalog, den);
            EntityMovePlan plan;
            try
            {
                plan = EntityMove.Plan(links, den, target, scope, id, EditOwn);
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

            // A link to it that still wouldn't reach it, even by id (another file has the same id, say).
            var lost = plan.Changes.Where(c => c.Before.Target?.Path == path).ToList();
            if (lost.Count > 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"{lost.Count} link(s) to '{target.Name}' wouldn't reach it after the move, even by its id: " +
                    $"{string.Join("; ", lost.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} would {Outcome(l.After)}"))}. Nothing was changed. Call check to see why.");
            }

            // Links that reached another entity and wouldn't any more, or would reach another
            // (a nearer scope wins a tie, F-LINK-7), refuse the move unless allow_relinks says
            // to go ahead (maintainer decision, 2026-10-26). That includes the moved file's own
            // links, whose scope chain changes. A link that was ambiguous or broken is only reported.
            var relinked = plan.Changes.Where(l => l.Before.Outcome == Core.Links.LinkOutcome.Resolved).ToList();
            if (relinked.Count > 0 && !allowRelinks)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"{relinked.Count} link(s) would change which entity they reach once '{target.Name}' is in {EntityMove.Describe(scope)}: " +
                    $"{string.Join("; ", relinked.Take(ListedLinkChanges).Select(l => $"{Where(l.After)}{(InFinalBook(l.Before) ? " (in a final book)" : "")} reaches {Reached(l.Before)} and would {Outcome(l.After)}"))}" +
                    $"{(relinked.Count > ListedLinkChanges ? $"; and {relinked.Count - ListedLinkChanges} more" : "")}. Nothing was changed. " +
                    "Link those by id or file name first, or pass allow_relinks: true to move it anyway.");
            }

            var frozenRelinks = relinked.Where(l => InFinalBook(l.Before)).ToList();
            if (frozenRelinks.Count > 0 && RefuseRelinksInFinalBooks)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen,
                    $"{frozenRelinks.Count} link(s) in a final (frozen) book reach another entity and would change target (F-BOOK-1): " +
                    $"{string.Join("; ", frozenRelinks.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} reaches {Reached(l.Before)}"))}. Nothing was changed.");
            }

            Save(files, plan.Originals, created: plan.To, removed: path);
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

            effects.AddRange(plan.Changes.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} {Now(l.After)}; before, it {Was(l.Before)}."));
            if (plan.Changes.Count > ListedLinkChanges)
            {
                effects.Add($"And {plan.Changes.Count - ListedLinkChanges} more link(s) resolve differently now. Call check to see them.");
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
    private sealed record EntryChange(Func<MarkdownFile, MarkdownFile> Edit, List<string> Effects, bool ChangesViews);

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

        var where = book is null ? null : WhereIn(book, scope);
        if (where is null && book is not null)
        {
            if (keepEntry || enterAtNewScope)
            {
                effects.Add($"It enters canon inside {EntityMove.Describe(scope)}, so {(keepEntry ? "keep_entry" : "enter_at_new_scope")} wasn't needed and changed nothing.");
            }

            if (toWrite is null)
            {
                return new EntryChange(f => f, effects, false);
            }

            effects.Insert(0, $"It had no 'from', so it entered canon at {entity.Scope.StartDescription}; that's now written as {written}, so canon views don't change (F-RT-5, F-ENT-6).");
            return new EntryChange(f => WithFrom(f, toWrite), effects, false);
        }

        var enters = entity.From is not null ? $"{entity.From} (its 'from')" : $"{entity.Scope.StartDescription} (it has no 'from')";
        if (enterAtNewScope)
        {
            effects.Add(
                $"It now enters canon at {scope.StartDescription} (enter_at_new_scope); before the move it entered at {enters}, " +
                "so canon views between the two points don't include it any more" + (entity.From is null ? "." : ", and its 'from' was taken out."));
            return new EntryChange(f => entity.From is null ? f : Apply(() => f.WithoutFrontmatterValue(["from"])), effects, true);
        }

        if (book is null)
        {
            throw new DenToolException(
                ToolErrorCodes.EntryOutsideScope,
                $"'{entity.Name}' has no 'from', so it enters canon at {entity.Scope.StartDescription}, and no position can name that point: no book comes at or after it in story order. " +
                $"Nothing was changed. Ask the writer, then call again with enter_at_new_scope: true to have it enter canon at {scope.StartDescription}, or give it a 'from' first with update_fields.");
        }

        if (keepEntry)
        {
            effects.Add(
                $"It still enters canon at {enters} (keep_entry), which is {where} {EntityMove.Describe(scope)}" +
                (toWrite is null ? "." : $"; that's now written as {written}.") +
                (scope is Book ? " A book entity whose 'from' is outside its book gets a warning (F-ENT-6)." : ""));
            return new EntryChange(f => toWrite is null ? f : WithFrom(f, toWrite), effects, false);
        }

        var already = where!.StartsWith("before", StringComparison.Ordinal)
            ? $"so the reader has already met it when {EntityMove.Describe(scope)} starts"
            : $"so the reader meets it only after {EntityMove.Describe(scope)}";
        throw new DenToolException(
            ToolErrorCodes.EntryOutsideScope,
            $"'{entity.Name}' enters canon at {enters}, in book '{book.Key}', which is {where} {EntityMove.Describe(scope)}, {already}. " +
            "Moving it there needs the writer's choice, so nothing was changed. Ask the writer, then call again with keep_entry: true to keep " +
            $"{written} (it still enters canon there{(scope is Book ? ", and check warns that its 'from' is outside its book, F-ENT-6" : "")}), " +
            $"or enter_at_new_scope: true to have it enter canon at {scope.StartDescription} instead, so canon views before then leave it out.");
    }

    /// <summary>Null if <paramref name="book"/> is in <paramref name="scope"/>; otherwise "before" or "after", as it comes in story order.</summary>
    private static string? WhereIn(Book book, Container scope)
    {
        var inside = scope switch
        {
            Universe => true,
            Series s => book.Series == s,
            _ => book == scope,
        };
        return inside ? null : book.Start < scope.Start ? "before the start of" : "after";
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
