using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Editing;

/// <summary>
/// What changing an entity's name or one of its aliases changes
/// (<see cref="EntityRename.Plan"/>): the entity's file, and every file whose
/// links that used the old text were rewritten, none of them written yet.
/// </summary>
/// <param name="Files">The new contents of each file that changes, by den-relative path: the entity's file first.</param>
/// <param name="Originals">What each of those files held when the plan was made, to check that nothing changed them before they're written.</param>
/// <param name="Rewritten">The links that were rewritten, as the den resolved them before the change.</param>
/// <param name="LinksRewritten">How many links were rewritten, in all files.</param>
/// <param name="FrozenPaths">Files with links that use the old text but are in a final book (F-BOOK-1), so they can't be rewritten; when there are any, the change mustn't be made.</param>
/// <param name="SkippedPaths">Files the den leaves out for errors (F-GEN-2) whose text holds a link with the old text as its target. They can't be edited safely, so they aren't rewritten, and the writer should be told.</param>
public sealed record EntityRenamePlan(
    IReadOnlyDictionary<string, byte[]> Files, IReadOnlyDictionary<string, byte[]> Originals, IReadOnlyList<ResolvedLink> Rewritten, int LinksRewritten,
    IReadOnlyList<string> FrozenPaths, IReadOnlyList<string> SkippedPaths);

/// <summary>
/// Changes an entity's <c>name</c> or one of its <c>aliases</c>, and
/// rewrites, in the same change, every link that matched the old text
/// (format.md F-RT-5, maintainer decision 2026-10-25): a link that reaches
/// the entity by name, for a name, or by alias, for an alias, and whose
/// target is the old text. A link that reaches it by id or slug doesn't
/// depend on the text, so it's left alone, and so is one that matched the
/// old text but doesn't reach the entity (it's ambiguous, or reaches
/// another entity in a nearer scope).
/// </summary>
public static class EntityRename
{
    /// <summary>The characters a link's target can't hold (format.md §10.1).</summary>
    private static readonly char[] NotInLinks = ['[', ']', '|', '#', '\r', '\n'];

    /// <summary>
    /// Plans the change. Nothing is written.
    /// </summary>
    /// <param name="links">The den's links, from the same load as <paramref name="entity"/>.</param>
    /// <param name="den">The den, to read outlines that hold links.</param>
    /// <param name="entity">The entity whose name or alias changes.</param>
    /// <param name="matchedBy">Which text changes: <see cref="LinkMatchKind.Name"/> or <see cref="LinkMatchKind.Alias"/>.</param>
    /// <param name="oldText">The name or alias as it is now.</param>
    /// <param name="linkText">The target the links that used <paramref name="oldText"/> get, or null to leave them as written (when the old text stays an alias, say).</param>
    /// <param name="editOwn">The edit of the entity's own file (its frontmatter, and its title), made after its links are rewritten.</param>
    /// <exception cref="ArgumentException">Links must be rewritten and <paramref name="linkText"/> can't be a link's target (it holds <c>[</c>, <c>]</c>, <c>|</c>, or <c>#</c>, or ends with a backslash).</exception>
    /// <exception cref="NotSupportedException">A file's links can't be rewritten in place.</exception>
    /// <exception cref="EditVerificationException">An edit didn't read back as intended (a bug).</exception>
    public static EntityRenamePlan Plan(
        LinkIndex links, IDenSource den, CatalogEntity entity, LinkMatchKind matchedBy, string oldText, string? linkText, Func<MarkdownFile, MarkdownFile> editOwn)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(editOwn);
        if (matchedBy is not (LinkMatchKind.Name or LinkMatchKind.Alias))
        {
            throw new ArgumentException("Only a name or an alias is renamed here.", nameof(matchedBy));
        }

        var targeting = linkText is null ? [] : UsingText(links, entity, matchedBy, oldText);
        if (targeting.Count > 0 && (linkText!.Trim().Length == 0 || linkText.IndexOfAny(NotInLinks) >= 0 || linkText.TrimEnd().EndsWith('\\')))
        {
            throw new ArgumentException(
                $"{targeting.Count} link(s) use '{oldText}', and a link can't name '{linkText}' (a link's target can't hold [, ], |, or #, or end with a backslash), so they couldn't follow. Choose other text.",
                nameof(linkText));
        }

        var result = LinkRewriter.Rewrite(
            links, den, entity, targeting, (target, _) => LinkText.Matches(target, oldText), text => LinkSyntax.WithTarget(text, linkText!), linkText ?? "", editOwn);
        var frozen = result.Frozen.ToHashSet(StringComparer.Ordinal);
        var skipped = linkText is null ? [] : LinkRewriter.Skipped(links, den, entity, (target, _) => LinkText.Matches(target, oldText));
        return new EntityRenamePlan(
            result.Files, result.Originals, targeting.Where(l => !frozen.Contains(l.Occurrence.Path)).ToList(), result.Count, result.Frozen, skipped);
    }

    /// <summary>The links that reach <paramref name="entity"/> in the tier <paramref name="matchedBy"/> with <paramref name="text"/> as their target.</summary>
    public static IReadOnlyList<ResolvedLink> UsingText(LinkIndex links, CatalogEntity entity, LinkMatchKind matchedBy, string text)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(text);
        return links.LinksTo(entity).Where(l => l.Resolution.MatchedBy == matchedBy && LinkText.Matches(l.Occurrence.Link.Target, text)).ToList();
    }
}

/// <summary>A link that resolves differently after a change: the same link, as resolved before and after.</summary>
/// <param name="Before">The link as the den resolved it before.</param>
/// <param name="After">The same link, perhaps rewritten, as it resolves after.</param>
public sealed record LinkChange(ResolvedLink Before, ResolvedLink After);

/// <summary>Compares how a den's links resolve before and after a change.</summary>
public static class LinkChanges
{
    /// <summary>
    /// The links whose outcome or target differs between
    /// <paramref name="before"/> and <paramref name="after"/>. A file's links
    /// are paired in order, so the change mustn't add or remove links; a file
    /// whose number of links differs is skipped. Entities are compared by path.
    /// </summary>
    public static IReadOnlyList<LinkChange> Between(LinkIndex before, LinkIndex after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var then = before.Links.GroupBy(l => l.Occurrence.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<LinkChange>();
        foreach (var group in after.Links.GroupBy(l => l.Occurrence.Path, StringComparer.Ordinal))
        {
            if (!then.TryGetValue(group.Key, out var old) || old.Count != group.Count())
            {
                continue;
            }

            foreach (var (b, a) in old.Zip(group))
            {
                if (b.Outcome != a.Outcome || b.Target?.Path != a.Target?.Path)
                {
                    result.Add(new LinkChange(b, a));
                }
            }
        }

        return result;
    }
}
