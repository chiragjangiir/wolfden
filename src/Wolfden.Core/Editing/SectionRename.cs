using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Editing;

/// <summary>
/// What renaming a section changes (<see cref="SectionRename.Plan"/>): the
/// entity's file with the new heading, and every file whose links to the
/// section were rewritten, none of them written yet.
/// </summary>
/// <param name="Files">The new contents of each file that changes, by den-relative path: the entity's file first.</param>
/// <param name="Originals">What each of those files held when the plan was made, to check that nothing changed them before they're written.</param>
/// <param name="LinksRewritten">How many links were rewritten, in all files.</param>
/// <param name="FrozenPaths">Files that link to the section but are in a final book (F-BOOK-1), so they can't be rewritten; when there are any, the rename mustn't be made.</param>
/// <param name="SkippedPaths">
/// Files the den leaves out for errors (F-GEN-2) whose text holds what looks
/// like a link to the section (its heading, and a target that is the entity's
/// id, slug, name, or an alias). They can't be edited safely, so they aren't
/// rewritten, and the writer should be told.
/// </param>
public sealed record SectionRenamePlan(
    IReadOnlyDictionary<string, byte[]> Files, IReadOnlyDictionary<string, byte[]> Originals, int LinksRewritten, IReadOnlyList<string> FrozenPaths,
    IReadOnlyList<string> SkippedPaths);

/// <summary>
/// Renames a section and rewrites, in the same change, every link to it by
/// heading (<c>[[entity#Old name]]</c>) in any file of the den (format.md
/// F-RT-5, maintainer decision 2026-10-23). A link is "to it" when it
/// resolves to the entity and its heading matches this section first
/// (F-LINK-9), so links to another section with the same name are left alone.
/// </summary>
public static class SectionRename
{
    /// <summary>The characters a heading in a link can't hold (format.md §10.1).</summary>
    private static readonly char[] NotInLinks = ['[', ']', '|', '#', '\r', '\n'];

    /// <summary>
    /// Plans the rename of <paramref name="section"/>, a section of
    /// <paramref name="entity"/>'s file as <paramref name="links"/> loaded it,
    /// to <paramref name="newName"/>. Nothing is written.
    /// </summary>
    /// <param name="links">The den's links, from the same load as <paramref name="entity"/>.</param>
    /// <param name="den">The den, to read outlines that hold links.</param>
    /// <param name="entity">The entity whose section is renamed.</param>
    /// <param name="section">The section, one of <c>entity.File.Sections</c>.</param>
    /// <param name="newName">The new heading text.</param>
    /// <exception cref="ArgumentException">The section isn't the entity's, the name isn't one line, or links to the section can't name it (it holds <c>[</c>, <c>]</c>, <c>|</c>, or <c>#</c>).</exception>
    /// <exception cref="NotSupportedException">A file's links can't be rewritten in place.</exception>
    /// <exception cref="EditVerificationException">An edit didn't read back as intended (a bug).</exception>
    public static SectionRenamePlan Plan(LinkIndex links, IDenSource den, CatalogEntity entity, Section section, string newName)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(newName);
        if (!ReferenceEquals(section.Owner, entity.File))
        {
            throw new ArgumentException("The section isn't one of the entity's.", nameof(section));
        }

        var targeting = links.LinksTo(entity).Where(l => ReferenceEquals(l.TargetSection, section)).ToList();
        if (targeting.Count > 0 && newName.IndexOfAny(NotInLinks) >= 0)
        {
            throw new ArgumentException(
                $"{targeting.Count} link(s) name '{section.Name}', and a link can't name a heading with [, ], |, or # in it, so they couldn't follow the rename. Choose a name without them.",
                nameof(newName));
        }

        // Links that reach another section of the new name would reach this
        // one instead if it comes first (F-LINK-9); links rewritten to the new
        // name would reach an earlier section of that name. Either changes
        // where existing links go, so the rename is refused.
        var taken = links.LinksTo(entity)
            .Where(l => l.TargetSection is { } other && !ReferenceEquals(other, section) && other.Index > section.Index
                && l.Occurrence.Link.Heading is { } h && LinkText.Matches(h, newName))
            .ToList();
        if (taken.Count > 0)
        {
            throw new ArgumentException(
                $"{taken.Count} link(s) name a later section, '{taken[0].TargetSection!.Name}' at line {taken[0].TargetSection!.Line}, which comes after '{section.Name}'. " +
                $"Renamed, '{section.Name}' would come first with that name and take those links (F-LINK-9). Choose another name.",
                nameof(newName));
        }

        if (targeting.Count > 0 && entity.File.Sections.FirstOrDefault(s => s.Index < section.Index && LinkText.Matches(s.Name, newName)) is { } earlier)
        {
            throw new ArgumentException(
                $"An earlier section, '{earlier.Name}' at line {earlier.Line}, already has that name, so the {targeting.Count} link(s) to '{section.Name}' would reach it instead once rewritten (F-LINK-9). Choose another name.",
                nameof(newName));
        }

        var result = LinkRewriter.Rewrite(
            links, den, entity, targeting, (_, heading) => heading is { } h && LinkText.Matches(h, section.Name), text => LinkSyntax.WithHeading(text, newName), newName,
            edited =>
            {
                var renamed = edited.Sections[section.Index];
                if (renamed.Name != section.Name)
                {
                    throw new EditVerificationException("Rewriting the links in the entity's own file moved its sections, so the rename was refused.");
                }

                return edited.WithSectionName(renamed, newName);
            });

        var keys = new[] { entity.Slug, entity.Name }.Concat(entity.Aliases).Where(k => k is not null).Select(k => LinkText.Normalize(k!)).ToHashSet(StringComparer.Ordinal);
        var skipped = LinkRewriter.Skipped(
            links, den, entity, (target, heading) => heading is { } h && LinkText.Matches(h, section.Name) && (target == entity.Id || keys.Contains(LinkText.Normalize(target))));
        return new SectionRenamePlan(result.Files, result.Originals, result.Count, result.Frozen, skipped);
    }
}
