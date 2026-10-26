using Wolfden.Core.Documents;
using Wolfden.Core.Editing;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Mcp;

/// <summary>
/// Renaming an entity, and changing or removing one of its aliases, with
/// the links that used the old text following in the same change (format.md
/// F-RT-5, maintainer decision 2026-10-25, #150).
/// </summary>
/// <remarks>
/// By default every link that reached the entity by the old text is
/// rewritten to the new text, in any file of the den, as
/// <see cref="RenameSection"/> rewrites heading links. Instead, the old text
/// can be kept as an alias, and then no link changes. If a link to rewrite
/// is in a final book, nothing changes, and the error suggests keeping the
/// old text, which leaves the frozen book alone. A rewrite that would send a
/// rewritten link somewhere else is refused; any other link that would
/// resolve differently afterwards (an alias kept that another entity also
/// has, F-LINK-7, say) is listed in the result's effects.
/// </remarks>
public sealed partial class DenTools
{
    /// <summary>How many links whose resolution changes are listed in effects, before "and N more".</summary>
    private const int ListedLinkChanges = 10;

    /// <summary>
    /// Changes an entity's <c>name</c>, and rewrites every link that reached
    /// it by the old name to the new one, in any file of the den (format.md
    /// F-RT-5). With <paramref name="keepOldName"/>, the old name becomes an
    /// alias instead, and no link changes. A display title (<c># Old name</c>)
    /// that reads the old name follows. The file isn't renamed.
    /// </summary>
    public WriteResult RenameEntity(string entity, string newName, string expectedHash, bool keepOldName = false, string? universe = null)
    {
        var hash = RequireHash(expectedHash);
        if (string.IsNullOrWhiteSpace(newName) || newName.Any(char.IsControl))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the entity's new name in 'new_name': one line of text.");
        }

        newName = newName.Trim();
        return ChangeText(entity, universe, hash, target =>
        {
            var oldName = target.Name;
            if (oldName == newName)
            {
                return null;
            }

            var sameForLinks = LinkText.Matches(oldName, newName);
            var keep = keepOldName && !sameForLinks;
            var alreadyAlias = target.Aliases.Any(a => LinkText.Matches(a, oldName));
            return new TextChange(
                LinkMatchKind.Name, oldName, newName, keep || sameForLinks ? null : newName,
                file =>
                {
                    file = Apply(() => file.WithFrontmatterValue(["name"], YamlScalarValue.String(newName)));
                    if (keep && !alreadyAlias)
                    {
                        file = Apply(() => file.WithNewListItem(["aliases"], YamlScalarValue.String(oldName), null, inTemplateOrder: true));
                    }

                    return file.Title is { } title && title.Trim() == oldName ? Apply(() => file.WithTitle(newName)) : file;
                },
                Kept: keep ? oldName : null,
                NewAlias: null,
                SameForLinks: sameForLinks,
                KeepHint: "pass keep_old_name: true to keep the old name as an alias instead, which leaves those links as they are");
        });
    }

    /// <summary>
    /// Changes one of an entity's aliases to <paramref name="newAlias"/>, or
    /// removes it when <paramref name="newAlias"/> is null, and rewrites every
    /// link that reached the entity by the old alias, in any file of the den
    /// (format.md F-RT-5): to the new alias, or, for a removed alias, to the
    /// entity's name. With <paramref name="keepOldAlias"/>, the new alias is
    /// added beside the old one instead, and no link changes.
    /// </summary>
    public WriteResult ChangeAlias(string entity, string alias, string expectedHash, string? newAlias = null, bool keepOldAlias = false, string? universe = null)
    {
        var hash = RequireHash(expectedHash);
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the alias to change in 'alias'.");
        }

        if (newAlias is not null && (string.IsNullOrWhiteSpace(newAlias) || newAlias.Any(char.IsControl)))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "An alias is one line of text, not empty (F-ENT-4). To remove the alias, leave out 'new_alias'.");
        }

        if (newAlias is null && keepOldAlias)
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument, "'keep_old_alias' keeps the old alias beside a new one: give the new one in 'new_alias'. Without it, the alias is removed.");
        }

        newAlias = newAlias?.Trim();
        return ChangeText(entity, universe, hash, target =>
        {
            var file = target.File;
            var list = file.GetFrontmatterValue("aliases") as YamlSequence;
            var index = list?.Items.ToList().FindIndex(i => i is YamlScalar { IsNull: false } s && LinkText.Matches(s.Text, alias.Trim())) ?? -1;
            if (index < 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"'{target.Name}' has no alias '{alias.Trim()}'. Its aliases: " +
                    (target.Aliases.Count == 0 ? "none." : string.Join(", ", target.Aliases.Select(a => $"'{a}'")) + "."));
            }

            var oldAlias = ((YamlScalar)list!.Items[index]).Text;
            if (newAlias == oldAlias)
            {
                return null;
            }

            var sameForLinks = newAlias is not null && LinkText.Matches(oldAlias, newAlias);
            if (newAlias is not null && !sameForLinks
                && (LinkText.Matches(target.Name, newAlias) || target.Aliases.Any(a => LinkText.Matches(a, newAlias))))
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"'{target.Name}' already goes by '{newAlias}' ({(LinkText.Matches(target.Name, newAlias) ? "its name" : "another alias")}). " +
                    $"To drop '{oldAlias}', call change_alias without 'new_alias', which points its links at the name.");
            }

            var itemPath = new[] { "aliases", index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var keep = keepOldAlias && !sameForLinks;
            return new TextChange(
                LinkMatchKind.Alias, oldAlias, newAlias, keep || sameForLinks ? null : newAlias ?? target.Name,
                f => keep ? Apply(() => f.WithNewListItem(["aliases"], YamlScalarValue.String(newAlias!), null, inTemplateOrder: true))
                    : newAlias is null ? Apply(() => f.WithoutFrontmatterValue(itemPath))
                    : Apply(() => f.WithFrontmatterValue(itemPath, YamlScalarValue.String(newAlias))),
                Kept: keep ? oldAlias : null,
                NewAlias: newAlias,
                SameForLinks: sameForLinks,
                KeepHint: newAlias is null
                    ? "keep the alias, or ask the writer to unfreeze the book"
                    : "pass keep_old_alias: true to add the new alias beside the old one instead, which leaves those links as they are");
        });
    }

    /// <summary>
    /// A change to a name or an alias.
    /// </summary>
    /// <param name="Kind">Whether it's the name or an alias.</param>
    /// <param name="OldText">The text as it is now.</param>
    /// <param name="NewText">The new text, or null for a removed alias.</param>
    /// <param name="LinkText">The target links that used the old text get, or null to leave them.</param>
    /// <param name="Edit">The edit of the entity's own file.</param>
    /// <param name="Kept">The old text, when it stays (as an alias).</param>
    /// <param name="NewAlias">For an alias: the new alias, or null if it's removed.</param>
    /// <param name="SameForLinks">True if the new text matches the old for links (F-LINK-4), so links needn't change.</param>
    /// <param name="KeepHint">What to do instead when a link to rewrite is in a final book.</param>
    private sealed record TextChange(
        LinkMatchKind Kind, string OldText, string? NewText, string? LinkText, Func<MarkdownFile, MarkdownFile> Edit, string? Kept, string? NewAlias, bool SameForLinks,
        string KeepHint);

    private WriteResult ChangeText(string entity, string? universe, string hash, Func<CatalogEntity, TextChange?> change)
    {
        var (path, ids) = Locate(entity, universe);
        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var catalog = EntityCatalog.Load(structure, den);
            var target = catalog.FindByPath(path) ?? throw (den.FileExists(path)
                ? new DenToolException(
                    ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be edited here. Call check with path '{path}' to see them.")
                : Gone(path));
            var original = target.File.ToBytes();
            CheckHash(path, original, hash);
            CheckNotFrozen(target);
            if (change(target) is not { } c)
            {
                return Unchanged(path, original, target);
            }

            var what = c.Kind == LinkMatchKind.Name ? "name" : "alias";
            var links = LinkIndex.Build(catalog, den);
            EntityRenamePlan plan;
            try
            {
                plan = EntityRename.Plan(links, den, target, c.Kind, c.OldText, c.LinkText, c.Edit);
            }
            catch (ArgumentException e)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, e.Message.Split(" (Parameter", 2)[0]);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or EditVerificationException)
            {
                throw new DenToolException(ToolErrorCodes.UnsupportedEdit, $"The change can't be made in place: {e.Message}");
            }

            if (plan.FrozenPaths.Count > 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen,
                    $"Links that use the {what} '{c.OldText}' are in files of a final (frozen) book, which can't be edited until it's unfrozen (F-BOOK-1): " +
                    $"{string.Join(", ", plan.FrozenPaths)}. Rewriting them is the only way they'd follow, so nothing was changed. To make the change, {c.KeepHint}.");
            }

            var files = plan.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (files[path].AsSpan().SequenceEqual(original) && files.Count == 1)
            {
                return Unchanged(path, original, target);
            }

            var idAdded = target.Id is null ? AddId(files, path, ids) : null;
            var check = Check(den, structure, files);

            // How every link resolves afterwards: a rewritten link must still reach the entity,
            // and any other link that resolves differently is said (F-LINK-6, F-LINK-7).
            var overlay = new OverlayDenSource(den, files);
            var afterLinks = LinkIndex.Build(EntityCatalog.Load(check.Structure, overlay), overlay);
            var changed = LinkChanges.Between(links, afterLinks);
            var rewritten = plan.Rewritten.ToHashSet();
            var lost = changed.Where(l => rewritten.Contains(l.Before)).ToList();
            if (lost.Count > 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    $"{lost.Count} link(s) that use '{c.OldText}' would be rewritten to [[{c.LinkText}]], which wouldn't reach '{target.Name}': " +
                    $"{string.Join("; ", lost.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} would {Outcome(l.After)}"))}" +
                    $"{(lost.Count > ListedLinkChanges ? $"; and {lost.Count - ListedLinkChanges} more" : "")}. Nothing was changed. " +
                    $"Choose other text, or {c.KeepHint}.");
            }

            Save(files, plan.Originals, created: null);
            var after = EntityCatalog.ReadEntityFile(check.Structure, path, files[path]).Entity!;
            var effects = new List<string> { Said(c, plan, target, files.Count) };
            if (c.Kind == LinkMatchKind.Name)
            {
                effects.Add($"The file keeps its name, {path[(path.LastIndexOf('/') + 1)..]}, so links by its file name still reach it; renaming the file is a separate change.");
                if (target.File.Title is { } title && title.Trim() == c.OldText)
                {
                    effects.Add($"Its title heading, '# {title}', now reads '# {c.NewText}' too.");
                }
            }

            foreach (var text in new[] { c.Kind == LinkMatchKind.Name && c.Kept is null ? c.NewText : null, c.Kept, c.NewAlias }.Where(t => t is not null).Distinct())
            {
                effects.AddRange(TextEffects(afterLinks.Catalog, after, text!, isName: c.Kind == LinkMatchKind.Name && text == c.NewText));
            }

            var moved = changed.Where(l => !rewritten.Contains(l.Before)).ToList();
            effects.AddRange(moved.Take(ListedLinkChanges).Select(l => $"{Where(l.After)} {Now(l.After)}; before, it {Was(l.Before)}."));
            if (moved.Count > ListedLinkChanges)
            {
                effects.Add($"And {moved.Count - ListedLinkChanges} more link(s) resolve differently now. Call check to see them.");
            }

            if (plan.SkippedPaths.Count > 0)
            {
                effects.Add(
                    $"These files have errors, so the den leaves them out and they weren't edited, but their text has links that use '{c.OldText}': " +
                    $"{string.Join(", ", plan.SkippedPaths)}. Once they're fixed, change those links to '{c.LinkText}' by hand.");
            }

            if (idAdded is not null)
            {
                effects.Add($"The file had no id, so it was given {idAdded} (F-ID-6).");
            }

            return new WriteResult(
                ToolJson.Version, true, path, HashOf(files[path]), Summary(after), effects, [], Warnings(check),
                files.OrderBy(p => p.Key == path ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new WrittenFileView(p.Key, HashOf(p.Value))).ToList(),
                IdAdded: idAdded);
        }
    }

    /// <summary>Gives the entity's planned file a new id, if it can be added in place (F-ID-6).</summary>
    private string? AddId(Dictionary<string, byte[]> files, string path, HashSet<string> ids)
    {
        var file = MarkdownFile.Parse(files[path], requireFrontmatter: true);
        var id = AddId(ref file, ids);
        files[path] = file.ToBytes();
        return id;
    }

    /// <summary>The first effect: what changed, and what became of the links.</summary>
    private static string Said(TextChange c, EntityRenamePlan plan, CatalogEntity target, int files)
    {
        var what = c.Kind == LinkMatchKind.Name ? "name" : "alias";
        var change = c.Kind == LinkMatchKind.Name ? $"Renamed '{c.OldText}' to '{c.NewText}'"
            : c.Kept is not null ? $"Added the alias '{c.NewText}' beside '{c.OldText}'"
            : c.NewText is null ? $"Removed the alias '{c.OldText}'"
            : $"Changed the alias '{c.OldText}' to '{c.NewText}'";
        if (c.SameForLinks)
        {
            return $"{change}. Links match it ignoring case and spacing (F-LINK-4), so the links that use '{c.OldText}' still reach it, and none were changed.";
        }

        if (c.Kept is not null)
        {
            return c.Kind == LinkMatchKind.Name
                ? $"{change}, and kept '{c.OldText}' as an alias, so links that use it still reach it, by alias now; none were changed."
                : $"{change}, so links that use either reach it; none were changed.";
        }

        return plan.LinksRewritten == 0
            ? $"{change}. No links used the old {what}."
            : $"{change}, and rewrote {plan.LinksRewritten} link(s) that used it to [[{c.LinkText}]]{(c.Kind == LinkMatchKind.Alias && c.NewText is null ? ", its name" : "")} " +
              $"(files written: {files}).";
    }

    /// <summary>
    /// What the new name, or a new or kept alias, means for links from now
    /// on: another entity of the universe with that text as its slug, name,
    /// or an alias (F-LINK-6, F-LINK-7).
    /// </summary>
    private static IEnumerable<string> TextEffects(EntityCatalog catalog, CatalogEntity entity, string text, bool isName)
    {
        var mine = isName ? "name" : "alias";
        foreach (var other in catalog.Entities.Where(e => e.Universe.Key == entity.Universe.Key && e.Path != entity.Path))
        {
            var tier = other.Slug is { } slug && LinkText.Matches(slug, text) ? "slug"
                : LinkText.Matches(other.Name, text) ? "name"
                : other.Aliases.Any(a => LinkText.Matches(a, text)) ? "alias" : null;
            if (tier is null)
            {
                continue;
            }

            yield return tier == "slug"
                ? $"'{text}' is also the file name of '{other.Name}' ({other.Path}), so a link [[{text}]] reaches '{other.Name}': a file name matches before a {mine} (F-LINK-6)."
                : tier == mine
                ? $"'{text}' is also the {tier} of '{other.Name}' ({other.Path}), so a link [[{text}]] is ambiguous between them unless one is in a nearer scope (F-LINK-7)."
                : tier == "name"
                ? $"'{text}' is also the name of '{other.Name}' ({other.Path}), so a link [[{text}]] reaches '{other.Name}': a name matches before an alias (F-LINK-6)."
                : $"'{text}' is also an alias of '{other.Name}' ({other.Path}), so a link [[{text}]] reaches '{entity.Name}': a name matches before an alias (F-LINK-6).";
        }
    }

    private static string Where(ResolvedLink link) => $"{link.Occurrence.Link.Text} in {link.Occurrence.Path} (line {link.Occurrence.Link.Line})";

    private static string Outcome(ResolvedLink link) => link.Outcome switch
    {
        Core.Links.LinkOutcome.Resolved => $"reach '{link.Target!.Name}' ({link.Target.Path})",
        Core.Links.LinkOutcome.Ambiguous => $"be ambiguous: {link.Resolution.Problem?.TrimEnd('.')}",
        _ => "be broken",
    };

    private static string Now(ResolvedLink link) => link.Outcome switch
    {
        Core.Links.LinkOutcome.Resolved => $"now reaches '{link.Target!.Name}' ({link.Target.Path})",
        Core.Links.LinkOutcome.Ambiguous => $"is now ambiguous (F-LINK-7): {link.Resolution.Problem?.TrimEnd('.')}",
        _ => "is now broken (F-LINK-8)",
    };

    private static string Was(ResolvedLink link) => link.Outcome switch
    {
        Core.Links.LinkOutcome.Resolved => $"reached '{link.Target!.Name}' ({link.Target.Path})",
        Core.Links.LinkOutcome.Ambiguous => "was ambiguous",
        _ => "was broken",
    };
}
