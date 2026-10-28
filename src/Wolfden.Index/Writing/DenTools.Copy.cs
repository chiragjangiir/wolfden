using Wolfden.Core.Documents;
using Wolfden.Core.Editing;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;
using Wolfden.Index.Reading;

namespace Wolfden.Index.Writing;

/// <summary>
/// Copying an entity to start a new one from it (#129): the copy gets a new
/// id, a name, and a file name of its own, so it never makes an F-ID-3
/// duplicate, and keeps everything else as the original has it.
/// </summary>
/// <remarks>
/// <para>
/// The copy keeps its type, fields, sections, status, and <c>from</c> and
/// <c>until</c> values byte for byte, and the links inside it are left as
/// they are. Its aliases aren't copied, since an alias usually belongs to
/// the original, and a title heading that reads the original's name takes
/// the new one. It's filed beside the original, or in another scope (in
/// the same universe or another), in the same folder below that scope's
/// <c>entities/</c>. Nothing is copied into a final book (F-BOOK-1);
/// copying out of one is fine.
/// </para>
/// <para>
/// Before anything is written, every link in the den is resolved again as
/// it would be with the copy there. One that reached an entity and would
/// reach another (the copy's name or file name wins in a nearer scope), or
/// become ambiguous (it ties with the copy), refuses the copy, unless
/// <c>allow_relinks</c> says to go ahead, as for a rename (maintainer
/// decision, 2026-10-26). A link that was broken or ambiguous and now
/// reaches the copy reached nothing before, so it's only reported. So are the copy's own links that don't reach what they reach
/// in the original, such as those that break in another universe (F-LINK-5,
/// F-LINK-8): copying is allowed, and they're the writer's to-do list
/// (maintainer decision, 2026-10-23).
/// </para>
/// </remarks>
public sealed partial class DenTools
{
    /// <summary>
    /// Copies an entity under a new name (#129). The copy has a new id no
    /// file in the den uses, a slug made from <paramref name="newName"/>
    /// (or <paramref name="slug"/>), and no aliases; everything else is the
    /// original's. Without a target, it goes beside the original; with
    /// <paramref name="toUniverse"/>, <paramref name="toSeries"/>, or
    /// <paramref name="toBook"/>, into that scope. With
    /// <paramref name="expectedHash"/>, the copy is refused if the original
    /// has changed since it was read.
    /// </summary>
    public WriteResult CopyEntity(
        string entity, string newName, string? universe = null, string? toUniverse = null, string? toSeries = null, string? toBook = null,
        string? slug = null, bool allowRelinks = false, string? expectedHash = null)
    {
        if (NewEntity.NameProblem(newName) is { } problem)
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument, $"Give the copy's name {Words.NewName}: one line of text, at most {NewEntity.MaxNameLength} characters. That one can't be used: {problem}.");
        }

        newName = newName.Trim();
        var hash = expectedHash is null ? null : RequireHash(expectedHash);
        var filter = Filter(toSeries, toBook);
        var (path, ids) = Locate(entity, universe);
        var moved = toUniverse is not null || filter is not null;
        var targetUniverse = !moved ? null : WithIndex(index =>
        {
            var db = index.Context;
            var u = ChooseUniverse(db, toUniverse ?? universe, null, allowAll: false)!;
            CheckFilter(db, u, filter, Words);
            return u.Key;
        });

        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var catalog = EntityCatalog.Load(structure, den);
            var source = catalog.FindByPath(path) ?? throw (den.FileExists(path)
                ? new DenToolException(
                    ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be copied. {ToolWords.Cap(Words.CheckFile(path))} to see them.")
                : Gone(path));
            if (hash is not null)
            {
                CheckHash(path, source.File.ToBytes(), hash);
            }

            var scope = targetUniverse is null ? source.Scope : TargetScope(structure, targetUniverse, filter);
            if (scope is Book { Status: BookStatus.Final } frozen)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen, $"Book '{frozen.Key}' is final (frozen), so nothing can be copied into it until it's unfrozen (F-BOOK-1).");
            }

            var newPath = CopyPath(den, source, scope, newName, slug);
            var id = NewUnusedId(ids);
            var file = source.File;
            file = source.Id is null
                ? Apply(() => file.WithId(id))
                : Apply(() => file.WithFrontmatterValue(["id"], YamlScalarValue.String(id)));
            file = Apply(() => file.WithFrontmatterValue(["name"], YamlScalarValue.String(newName)));
            var droppedComments = 0;
            if (source.Aliases.Count > 0)
            {
                // Comment lines inside a block list go with it, and may hold a commented-out alias.
                var comments = CommentLines(file);
                file = Apply(() => file.WithoutFrontmatterValue(["aliases"]));
                droppedComments = comments - CommentLines(file);
            }

            var retitled = file.Title is { } title && title.Trim() == source.Name && source.Name != newName;
            if (retitled)
            {
                file = Apply(() => file.WithTitle(newName));
            }

            var bytes = file.ToBytes();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [newPath] = bytes };
            var check = Check(den, structure, files);

            // Every link resolved again with the copy there (F-LINK-6, F-LINK-7).
            var links = LinkIndex.Build(catalog, den);
            var overlay = new OverlayDenSource(den, files);
            var afterLinks = LinkIndex.Build(EntityCatalog.Load(check.Structure, overlay), overlay);
            var changed = LinkChanges.Between(links, afterLinks);
            // A link that reached an entity and would reach another, or become ambiguous, refuses the copy
            // unless allow_relinks, as for a rename (maintainer decision, 2026-10-26). A broken or ambiguous
            // link that now reaches the copy reached nothing before, so it's only reported.
            var relinked = changed.Where(l => l.Before.Outcome == LinkOutcome.Resolved
                && (l.After.Outcome != LinkOutcome.Resolved || l.After.Target!.Path != l.Before.Target!.Path)).ToList();
            if (relinked.Count > 0 && !allowRelinks)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"{relinked.Count} link(s) would change which entity they reach: " +
                    $"{string.Join("; ", relinked.Take(ListedLinkChanges).Select(l => $"{Where(l.After)}{(InFinalBook(l.Before) ? " (in a final book)" : "")} reaches {Reached(l.Before)} and would {Outcome(l.After)}"))}" +
                    $"{(relinked.Count > ListedLinkChanges ? $"; and {relinked.Count - ListedLinkChanges} more" : "")}. Nothing was copied. " +
                    $"Choose another name (or {Words.Slug}), link those by id first, or {Words.AllowRelinks} to make the copy anyway.");
            }

            var frozenRelinks = relinked.Where(l => InFinalBook(l.Before)).ToList();
            if (frozenRelinks.Count > 0 && RefuseRelinksInFinalBooks)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen,
                    $"{frozenRelinks.Count} link(s) in a final (frozen) book would reach the copy (F-BOOK-1): " +
                    $"{string.Join("; ", frozenRelinks.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} reaches {Reached(l.Before)}"))}. Nothing was copied. Choose another name.");
            }

            Save(files, null, created: newPath);
            var after = afterLinks.Catalog.FindByPath(newPath) ?? EntityCatalog.ReadEntityFile(check.Structure, newPath, bytes).Entity!;
            var effects = CopyEffects(source, after, scope, id, retitled, droppedComments, check, links, afterLinks, changed);
            return new WriteResult(
                DenReader.ResultVersion, true, newPath, HashOf(bytes), Summary(after), effects, [], Warnings(check),
                [new WrittenFileView(newPath, HashOf(bytes), Created: true)]);
        }
    }

    /// <summary>The universe, series, or book a copy goes into.</summary>
    private static Container TargetScope(DenStructure structure, string universe, CanonFilter? filter)
    {
        var u = structure.FindUniverse(universe) ?? throw Gone($"universes/{universe}");
        return filter is null ? u
            : filter.Kind == CanonFilterKind.Series
                ? u.Series.SingleOrDefault(s => s.Key == filter.Key) ?? throw new DenToolException(ToolErrorCodes.UnknownScope, $"Universe '{u.Key}' has no series '{filter.Key}' now.")
                : u.FindBook(filter.Key) ?? throw new DenToolException(ToolErrorCodes.UnknownScope, $"Universe '{u.Key}' has no book '{filter.Key}' now.");
    }

    /// <summary>
    /// The copy's path: the original's folder below its scope's
    /// <c>entities/</c> (its type's folder, and any folders below it), in
    /// <paramref name="scope"/>, and a slug made from the name and made
    /// unique there (F-TPL-6), or the one asked for, which must be free.
    /// </summary>
    private static string CopyPath(IDenSource den, CatalogEntity source, Container scope, string newName, string? slug)
    {
        var below = source.Path[(source.Scope.Path.Length + "/entities/".Length)..];
        var slash = below.LastIndexOf('/');
        var folder = $"{scope.Path}/entities" + (slash < 0 ? "" : "/" + below[..slash]);
        if (slug is not null)
        {
            slug = slug.Trim();
            if (!Keys.IsKey(slug))
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument, $"'{slug}' isn't a valid slug: lowercase letters and digits, with single hyphens between words, up to 64 characters.");
            }

            if (DenPaths.Problem($"{folder}/{slug}.md") is { } problem)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"'{slug}' can't be a file name: {problem}.");
            }

            if (NewEntity.IsTaken(den, folder, slug))
            {
                throw new DenToolException(ToolErrorCodes.AlreadyExists, $"'{folder}/{slug}.md' is taken: there's already a file of that name (compared ignoring case).");
            }

            return $"{folder}/{slug}.md";
        }

        var made = NewEntity.Slugify(newName);
        if (made.Length == 0)
        {
            made = Keys.IsKey(source.Type) ? source.Type : "entity";
        }

        return $"{folder}/{NewEntity.Unique(den, folder, made)}.md";
    }

    /// <summary>What the copy means: where it is, what it didn't take, when it enters canon, and the links it changes or that change for it.</summary>
    private List<string> CopyEffects(
        CatalogEntity source, CatalogEntity copy, Container scope, string id, bool retitled, int droppedComments, DenWriteCheckResult check,
        LinkIndex links, LinkIndex afterLinks, IReadOnlyList<LinkChange> changed)
    {
        var where = scope == source.Scope ? "beside it" : $"in {Describe(scope)}";
        var effects = new List<string>
        {
            $"Copied '{source.Name}' ({source.Path}) to {copy.Path}, {where}, as '{copy.Name}', with the new id {id}. " +
            "Its type, fields, sections, status, and from and until are the original's, and the links in it are as they were. The original is unchanged.",
        };
        if (source.Aliases.Count > 0)
        {
            effects.Add(
                $"The original's aliases ({string.Join(", ", source.Aliases.Select(a => $"'{a}'"))}) weren't copied, so links by them still reach the original.");
            if (droppedComments > 0)
            {
                effects.Add($"Removed {droppedComments} comment line{(droppedComments == 1 ? "" : "s")} inside aliases, which went with them.");
            }
        }

        if (retitled)
        {
            effects.Add($"Its title heading reads '# {copy.Name}', where the original's reads the original's name.");
        }

        if (scope.Universe != source.Universe)
        {
            effects.Add(
                $"It's in universe '{scope.Universe.Key}' now, and links reach only entities of their own universe (F-LINK-5), so the copy's links are " +
                $"resolved there, and positions in it name books of '{scope.Universe.Key}'.");
        }

        if (scope != source.Scope && source.From is null)
        {
            effects.Add($"It has no 'from', so it enters canon at {scope.StartDescription} (F-ENT-6); the original enters at {source.Scope.StartDescription}.");
        }
        else if (scope != source.Scope && scope is Book book && copy.FromResolution?.Resolved is { } from && from.Book.Path != book.Path)
        {
            effects.Add(
                $"Its 'from', {copy.From}, is in book '{from.Book.Key}', outside book '{book.Key}', where it's filed now: check reports that (F-ENT-6). " +
                "Change it to where the reader meets it in this book, or file the copy elsewhere.");
        }
        else if (scope != source.Scope && copy.FromResolution?.IsResolved == true)
        {
            effects.Add($"Its 'from' is still {copy.From}, so it enters canon there, as the original does (F-ENT-6).");
        }

        // Positions that resolved in the original and don't where the copy is (F-POS-4), such as those of another universe's books.
        var lost = UnresolvedPositions(copy).Except(UnresolvedPositions(source), StringComparer.Ordinal).ToList();
        if (lost.Count > 0)
        {
            effects.Add(
                $"{lost.Count} position(s) in it don't resolve where it is now, so check reports them (F-POS-4) and the facts that use them are left out of canon views: " +
                $"{string.Join(", ", lost.Take(ListedLinkChanges))}{(lost.Count > ListedLinkChanges ? $", and {lost.Count - ListedLinkChanges} more" : "")}. " +
                $"Change them to positions of universe '{scope.Universe.Key}'.");
        }

        effects.AddRange(TextEffects(afterLinks.Catalog, copy, copy.Name, isName: true));

        // The copy's own links, paired in order with the original's.
        var mine = links.Links.Where(l => l.Occurrence.Path == source.Path).ToList();
        var its = afterLinks.Links.Where(l => l.Occurrence.Path == copy.Path).ToList();
        if (mine.Count == its.Count)
        {
            var differ = mine.Zip(its).Where(p => p.First.Outcome != p.Second.Outcome || p.First.Target?.Path != p.Second.Target?.Path).ToList();
            if (differ.Count > 0)
            {
                var broken = differ.Count(p => p.Second.Outcome == LinkOutcome.Broken);
                effects.Add(
                    $"{differ.Count} link(s) in the copy don't reach what they reach in the original" +
                    (broken > 0 ? $", {broken} of them broken (F-LINK-8): write those notes, or change the links" : "") + ".");
                effects.AddRange(differ.Take(ListedLinkChanges).Select(p => $"{Where(p.Second)} {Now(p.Second)}; in the original, it {Was(p.First)}."));
                if (differ.Count > ListedLinkChanges)
                {
                    effects.Add($"And {differ.Count - ListedLinkChanges} more. {ToolWords.Cap(Words.CheckDen)} to see them.");
                }
            }
        }
        else
        {
            // The copy's links can't be paired with the original's (a bug, or a link in an alias), so they're counted instead.
            var unresolved = its.Count(l => l.Outcome != LinkOutcome.Resolved);
            effects.Add(
                $"The copy has {its.Count} link(s) and the original {mine.Count}, so they can't be compared one by one: " +
                $"{unresolved} of the copy's don't resolve where it is now. {ToolWords.Cap(Words.CheckDen)} to see them.");
        }

        var toOriginal = its.Count(l => l.Outcome == LinkOutcome.Resolved && l.Target!.Path == source.Path);
        if (toOriginal > 0)
        {
            effects.Add($"{toOriginal} link(s) in the copy reach the original, '{source.Name}', as they did in it. Change them if they should reach the copy.");
        }

        // Links elsewhere that resolve differently with the copy there.
        effects.AddRange(changed.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} {Now(l.After)}; before, it {Was(l.Before)}."));
        if (changed.Count > ListedLinkChanges)
        {
            effects.Add($"And {changed.Count - ListedLinkChanges} more link(s) resolve differently now. {ToolWords.Cap(Words.CheckDen)} to see them.");
        }

        return effects;
    }

    /// <summary>How many lines of the frontmatter are YAML comment lines.</summary>
    private static int CommentLines(MarkdownFile file) => file.FrontmatterText.Split('\n').Count(l => l.TrimStart().StartsWith('#'));

    /// <summary>The distinct positions written in the entity's file that don't resolve in its universe.</summary>
    private static IEnumerable<string> UnresolvedPositions(CatalogEntity entity) =>
        PositionScanner.Scan(entity.File, entity.Definition?.PositionFields).References
            .Where(r => r.Position is { } p && !entity.Universe.Resolve(p).IsResolved)
            .Select(r => r.Text)
            .Distinct(StringComparer.Ordinal);

    private static string Describe(Container scope) => scope.Kind switch
    {
        ContainerKind.Universe => $"universe '{scope.Key}'",
        ContainerKind.Series => $"series '{scope.Key}' of universe '{scope.Universe.Key}'",
        _ => $"book '{scope.Key}' of universe '{scope.Universe.Key}'",
    };
}
