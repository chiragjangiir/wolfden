using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Editing;

/// <summary>
/// What moving an entity's file to another scope changes
/// (<see cref="EntityMove.Plan"/>): the file at its new path, and every file
/// whose links to it were rewritten to its id, none of them written yet.
/// </summary>
/// <param name="From">The entity file's path now.</param>
/// <param name="To">Its path after the move.</param>
/// <param name="Files">The new contents of each file to write, by den-relative path: the moved file at <paramref name="To"/> first. <paramref name="From"/> isn't here: the file there moves to <paramref name="To"/>.</param>
/// <param name="Originals">What each file that changes held when the plan was made, <paramref name="From"/> included, to check that nothing changed them before they're written.</param>
/// <param name="After">The den's links as they'd resolve after the move.</param>
/// <param name="Changes">The links that would resolve differently after the move, by entity (the moved one counts as the same entity at either path). A link that was rewritten to the id and still reaches the entity isn't here.</param>
/// <param name="Rewritten">The links to the entity that were rewritten to its id, as the den resolved them before.</param>
/// <param name="FrozenPaths">Files with links to the entity that would stop reaching it but are in a final book (F-BOOK-1), so they can't be rewritten; when there are any, the move mustn't be made.</param>
/// <param name="SkippedPaths">Files the den leaves out for errors (F-GEN-2) whose text holds a link that could be to the entity, by its id, slug, name, or an alias. Their links can't be resolved, so the writer should check them.</param>
/// <param name="Interim">
/// What the file at <paramref name="From"/> holds before it's moved, for a
/// writer that rewrites it in place first and then moves it whole, so the
/// den is never without the entity or with two copies of it: its own links
/// rewritten, with the interim edit (its id, and a <c>from</c> that keeps
/// views as they are) instead of the final one.
/// </param>
public sealed record EntityMovePlan(
    string From, string To, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyDictionary<string, byte[]> Originals, LinkIndex After,
    IReadOnlyList<LinkChange> Changes, IReadOnlyList<ResolvedLink> Rewritten, IReadOnlyList<string> FrozenPaths, IReadOnlyList<string> SkippedPaths,
    byte[] Interim);

/// <summary>
/// Moves an entity's file to another scope of its universe: the universe,
/// a series, or a book (format.md F-LAY-5). The file keeps its place under
/// <c>entities/</c> (<c>characters/crew/mara-voss.md</c>), so its slug stays
/// the same. Links don't depend on scope except to break ties (F-LINK-7), so
/// a link to the entity that would stop reaching it after the move is
/// rewritten, in the same change, to the entity's id (F-RT-5), showing the
/// same text as before.
/// </summary>
/// <remarks>
/// Positions resolve across the universe (F-POS-4), so a move changes no
/// <c>from</c> or <c>until</c>. Only the default of a missing entity-level
/// <c>from</c> depends on scope (F-ENT-6); writing it explicitly, when it
/// must be kept, is the caller's edit of the file (<c>editOwn</c>).
/// </remarks>
public static class EntityMove
{
    /// <summary>
    /// Where <paramref name="entity"/>'s file goes in <paramref name="scope"/>:
    /// the same path below <c>entities/</c>, under the scope's folder.
    /// </summary>
    public static string Destination(CatalogEntity entity, Container scope)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(scope);
        var prefix = $"{entity.Scope.Path}/entities/";
        if (!entity.Path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{entity.Path}' isn't under its scope's entities folder.", nameof(entity));
        }

        return $"{scope.Path}/entities/{entity.Path[prefix.Length..]}";
    }

    /// <summary>
    /// Plans the move. Nothing is written.
    /// </summary>
    /// <param name="links">The den's links, from the same load as <paramref name="entity"/>.</param>
    /// <param name="den">The den.</param>
    /// <param name="entity">The entity to move.</param>
    /// <param name="scope">The scope it moves to, of the same universe.</param>
    /// <param name="id">The entity's id once <paramref name="editOwn"/> is made (it may add one), or null if it has none.</param>
    /// <param name="editOwn">The edit of the entity's own file (its <c>from</c>, a missing id), made after its links are rewritten.</param>
    /// <param name="interimEdit">The edit for <see cref="EntityMovePlan.Interim"/>, the file in place before it moves, if it differs from <paramref name="editOwn"/>.</param>
    /// <exception cref="ArgumentException">The scope is the entity's own, or in another universe.</exception>
    /// <exception cref="InvalidOperationException">Links to the entity would stop reaching it, and it has no id to rewrite them to.</exception>
    /// <exception cref="NotSupportedException">A file's links can't be rewritten in place.</exception>
    /// <exception cref="EditVerificationException">An edit didn't read back as intended (a bug).</exception>
    public static EntityMovePlan Plan(LinkIndex links, IDenSource den, CatalogEntity entity, Container scope, string? id, Func<MarkdownFile, MarkdownFile> editOwn,
        Func<MarkdownFile, MarkdownFile>? interimEdit = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(editOwn);
        if (scope.Universe != entity.Universe)
        {
            throw new ArgumentException("Entities don't move between universes (design.md §2).", nameof(scope));
        }

        if (scope == entity.Scope)
        {
            throw new ArgumentException($"'{entity.Name}' is filed in {Describe(scope)} already.", nameof(scope));
        }

        var from = entity.Path;
        var to = Destination(entity, scope);

        // First, the move alone: which links to the entity would stop reaching it?
        var moved = editOwn(entity.File).ToBytes();
        var first = AfterMove(links, den, from, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [to] = moved });
        var lost = Between(links, first, from, to).Where(c => c.Before.Target?.Path == from && !Reaches(c.After, to)).Select(c => c.Before).ToList();
        if (lost.Count > 0 && id is null)
        {
            throw new InvalidOperationException(
                $"{lost.Count} link(s) to '{entity.Name}' would stop reaching it, and it has no id to link them by (F-ID-6), so they couldn't follow.");
        }

        // Then those links rewritten to its id, in the same change (F-RT-5).
        var result = LinkRewriter.Rewrite(
            links, den, entity, lost, (_, _) => true, (text, inTableRow) => LinkSyntax.WithIdTarget(text, id!, inTableRow), $"{id}|", editOwn);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [to] = result.Files[from] };
        foreach (var (path, bytes) in result.Files.Where(p => p.Key != from))
        {
            files[path] = bytes;
        }

        var after = AfterMove(links, den, from, files);
        var frozen = result.Frozen.ToHashSet(StringComparer.Ordinal);
        var keys = new[] { entity.Slug, entity.Name }.Concat(entity.Aliases).Where(k => k is not null).Select(k => LinkText.Normalize(k!)).ToHashSet(StringComparer.Ordinal);
        var skipped = LinkRewriter.Skipped(links, den, entity, (target, _) => target == entity.Id || keys.Contains(LinkText.Normalize(target)));
        var interim = interimEdit is null ? result.Files[from]
            : LinkRewriter.Rewrite(
                links, den, entity, lost.Where(l => l.Occurrence.Path == from).ToList(), (_, _) => true,
                (text, inTableRow) => LinkSyntax.WithIdTarget(text, id!, inTableRow), $"{id}|", interimEdit).Files[from];
        return new EntityMovePlan(
            from, to, files, result.Originals, after, Between(links, after, from, to), lost.Where(l => !frozen.Contains(l.Occurrence.Path)).ToList(), result.Frozen, skipped,
            interim);
    }

    /// <summary>
    /// The links whose outcome or target differs between <paramref name="before"/>
    /// and <paramref name="after"/>, where the file at <paramref name="from"/>
    /// has moved to <paramref name="to"/>: its links are paired with the moved
    /// file's, and a link to it counts as the same target at either path.
    /// A file's links are paired in order, as for <see cref="LinkChanges.Between"/>.
    /// </summary>
    public static IReadOnlyList<LinkChange> Between(LinkIndex before, LinkIndex after, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        string Mapped(string path) => path == from ? to : path;
        var then = before.Links.GroupBy(l => Mapped(l.Occurrence.Path), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<LinkChange>();
        foreach (var group in after.Links.GroupBy(l => l.Occurrence.Path, StringComparer.Ordinal))
        {
            if (!then.TryGetValue(group.Key, out var old) || old.Count != group.Count())
            {
                continue;
            }

            foreach (var (b, a) in old.Zip(group))
            {
                var target = b.Target is { } t ? Mapped(t.Path) : null;
                if (b.Outcome != a.Outcome || target != a.Target?.Path)
                {
                    result.Add(new LinkChange(b, a));
                }
            }
        }

        return result;
    }

    /// <summary>Describes a scope for messages, such as "series 'long-dark'" or "the universe".</summary>
    public static string Describe(Container scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Kind == ContainerKind.Universe ? $"universe '{scope.Key}' itself" : $"{scope.Kind.ToString().ToLowerInvariant()} '{scope.Key}'";
    }

    private static bool Reaches(ResolvedLink link, string path) => link.Outcome == LinkOutcome.Resolved && link.Target!.Path == path;

    /// <summary>The den's links with <paramref name="files"/> written and the file at <paramref name="from"/> gone.</summary>
    private static LinkIndex AfterMove(LinkIndex links, IDenSource den, string from, Dictionary<string, byte[]> files)
    {
        var overlay = new OverlayDenSource(den, files, [from]);
        return LinkIndex.Build(EntityCatalog.Load(links.Catalog.Structure, overlay), overlay);
    }
}
