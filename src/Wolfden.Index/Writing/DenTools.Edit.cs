using System.Text;
using System.Text.RegularExpressions;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Text;
using Wolfden.Core.Yaml;
using Wolfden.Index.Reading;

namespace Wolfden.Index.Writing;

/// <summary>
/// The edits the web UI makes (#28): an entity's source, for an editor, and
/// edits of its lead, its sections, or its whole file. They're made through
/// the same edit loop as the MCP write tools (<see cref="EditEntity"/>), so
/// they're checked as the den reads the file (<c>invalid_edit</c>), refused
/// on a stale hash (<c>stale_file</c>) or in a final book (<c>frozen</c>),
/// give a missing id (F-ID-6), and list warnings, effects, and the pinned
/// canon they touch.
/// </summary>
/// <remarks>
/// A section is named by its place among the file's sections (its index, in
/// file order, subsections included) together with its name, both from
/// <see cref="GetSource"/> and checked against the file the hash names, so
/// a stale page or two sections of one name can't send an edit to the wrong
/// one.
/// </remarks>
public sealed partial class DenTools
{
    /// <summary>
    /// An entity's file as an editor needs it: its text and hash, read from
    /// the file rather than the index, its lead and each section's own text
    /// as written (line breaks as LF, without the blank lines around it),
    /// and whether it's frozen.
    /// </summary>
    public EntitySourceResult GetSource(string entity, string? universe = null)
    {
        var (path, _) = Locate(entity, universe);
        var den = Snapshot();
        var structure = DenStructure.Load(den);
        var bytes = den.TryReadFile(path) ?? throw Gone(path);
        var read = EntityCatalog.ReadEntityFile(structure, path, bytes);
        var e = read.Entity ?? throw new DenToolException(
            ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be edited here. {ToolWords.Cap(Words.CheckFile(path))} to see them.");
        return SourceOf(e, bytes);
    }

    /// <summary>The source of <paramref name="entity"/>, whose file holds <paramref name="bytes"/>.</summary>
    private static EntitySourceResult SourceOf(CatalogEntity entity, byte[] bytes)
    {
        var file = entity.File;
        var facts = EntityFacts.For(entity, new Bookkeeping(entity.Universe.Den.BookkeepingSections));
        return new EntitySourceResult(
            DenReader.ResultVersion,
            Summary(entity),
            HashOf(bytes),
            Lf(file.Text),
            file.LineEndings switch { LineEndingKind.CrLf => "crlf", LineEndingKind.Mixed => "mixed", _ => "lf" },
            file.HasByteOrderMark,
            FrozenReason(entity),
            new SourceLead(file.LeadLine, EditorText(file.Lead)),
            file.Sections.Select(s => new SourceSection(
                s.Index, s.Name, s.Level, s.Line, s.Comment?.Text, EditorText(s.Body), facts[s].Pinned, s.Parent?.Index)).ToList(),
            entity.Definition?.Sections.Select(d => d.Name).ToList() ?? []);
    }

    /// <summary>
    /// Replaces the entity's lead: the text before its first section, title
    /// heading included (format.md F-SEC-2). The blank lines around it, the
    /// frontmatter, and every section are kept as written.
    /// </summary>
    public WriteResult ReplaceLead(string entity, string text, string expectedHash, string? universe = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EditEntity(entity, universe, RequireHash(expectedHash), (file, _, pinned) =>
        {
            // The same text, line breaks aside, writes nothing, even in a file that mixes them.
            if (EditorText(file.Lead) != EditorText(text))
            {
                file = Apply(() => file.WithLead(text));
            }

            return (file, (before, after) =>
            {
                pinned.AddRange(PinnedChanges(before, after));
                var effects = new List<string> { "Replaced the text before the sections." };
                if (before.File.Title != after.File.Title)
                {
                    effects.Add(after.File.Title is { } title
                        ? $"The title heading now reads '{title}'. The entity's name is still '{after.Name}': that's its 'name' field (F-SEC-2)."
                        : "The lead has no title heading now. The entity's name is still its 'name' field.");
                }

                return effects;
            });
        });
    }

    /// <summary>
    /// Replaces the own text of the section at <paramref name="index"/>
    /// (which must be named <paramref name="name"/>): what follows its
    /// heading and section comment up to the next heading. The heading, the
    /// section comment, its subsections, and the blank lines around the text
    /// are kept.
    /// </summary>
    public WriteResult ReplaceSection(string entity, int index, string name, string text, string expectedHash, string? universe = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EditEntity(entity, universe, RequireHash(expectedHash), (file, _, pinned) =>
        {
            var target = SectionAt(file, index, name);
            if (EditorText(target.Body) != EditorText(text))
            {
                file = Apply(() => file.WithSectionBody(target, text));
            }

            return (file, (before, after) =>
            {
                pinned.AddRange(PinnedChanges(before, after));
                return [$"Replaced the text of '{target.Name}' (line {target.Line})."];
            });
        });
    }

    /// <summary>
    /// Adds a section named <paramref name="name"/> with <paramref name="text"/>:
    /// just before the section at <paramref name="beforeIndex"/> (named
    /// <paramref name="beforeName"/>), at that section's level, or at the end
    /// of the file as a level-2 section if no place is given.
    /// </summary>
    public WriteResult AddSectionAt(
        string entity, string name, string text, string expectedHash, string? universe = null, int? beforeIndex = null, string? beforeName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        name = SectionName(name);
        if (beforeIndex is null != beforeName is null)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give both the index and the name of the section the new one goes before, or neither for the end.");
        }

        return EditEntity(entity, universe, RequireHash(expectedHash), (file, catalog, pinned) =>
        {
            var before = beforeIndex is { } i ? SectionAt(file, i, beforeName!) : null;
            var level = before?.Level ?? 2;
            var existing = file.Sections.FirstOrDefault(s => LinkText.Matches(s.Name, name));
            file = Apply(() => file.WithNewSection(name, text, level, before));
            return (file, (_, after) =>
            {
                pinned.AddRange(PinnedChanges(catalog, after));
                var effects = new List<string>
                {
                    before is null ? $"Added the section '{name}' at the end." : $"Added the section '{name}' before '{before.Name}'.",
                };
                if (existing is not null)
                {
                    effects.Add(
                        $"The note already has a section '{existing.Name}' (line {existing.Line}), and a link to '{name}' reaches the first one (F-LINK-9).");
                }

                if (level == 2 && after.Definition is { } type && type.IsStandardSection(name) && existing is not null && existing.Level == 2)
                {
                    effects.Add(
                        $"'{name}' is a standard section of type '{type.Key}'. Without a 'from' in its section comment, the new one starts where the note does, " +
                        "as the earlier one does, so they overlap (F-SEC-4). Add one with Edit file.");
                }

                if (!after.Status.IsInCanon())
                {
                    effects.Add($"The note is '{after.Status.ToKey()}', so canon views leave the section out with it (F-STAT-3).");
                }

                return effects;
            });
        });
    }

    /// <summary>
    /// Renames the section at <paramref name="index"/> (which must be named
    /// <paramref name="name"/>), and rewrites every link to it by heading in
    /// any file of the den, as <see cref="RenameSection(string, string, string, string, string?, int?)"/> does.
    /// </summary>
    public WriteResult RenameSectionAt(string entity, int index, string name, string newName, string expectedHash, string? universe = null)
    {
        var hash = RequireHash(expectedHash);
        return RenameSection(entity, file => SectionAt(file, index, name), SectionName(newName), hash, universe);
    }

    /// <summary>
    /// Removes the section at <paramref name="index"/> (which must be named
    /// <paramref name="name"/>), with its heading, section comment, text, and
    /// subsections. Links to it by heading aren't rewritten; the result says
    /// which there are, and what they reach now.
    /// </summary>
    public WriteResult RemoveSection(string entity, int index, string name, string expectedHash, string? universe = null)
    {
        var hash = RequireHash(expectedHash);
        var (path, _) = Locate(entity, universe);
        var headingLinks = WithIndex(index =>
        {
            var record = index.Context.Entities.SingleOrDefault(e => e.Path == path);
            return record is null
                ? []
                : index.Backlinks(record.Key).Where(l => l.Heading != null).Select(l => (Heading: l.Heading!, l.Path, l.Line)).ToList();
        });

        return EditEntity(entity, universe, hash, (file, _, pinned) =>
        {
            var target = SectionAt(file, index, name);
            var removed = file.Sections.Where(s => s == target || IsUnder(s, target)).ToHashSet();
            var reaching = headingLinks
                .Where(l => file.Sections.FirstOrDefault(s => LinkText.Matches(s.Name, l.Heading)) is { } reached && removed.Contains(reached))
                .ToList();
            file = Apply(() => file.WithoutSection(target));
            var left = file;
            return (file, (before, after) =>
            {
                pinned.AddRange(PinnedChanges(before, after));
                var subsections = removed.Count - 1;
                var effects = new List<string>
                {
                    $"Removed the section '{target.Name}' (line {target.Line})" +
                    (subsections == 0 ? "." : $", with its {subsections} subsection{(subsections == 1 ? "" : "s")}."),
                };
                foreach (var group in reaching.GroupBy(l => l.Heading, StringComparer.Ordinal))
                {
                    var where = string.Join(", ", group.Select(l => $"{l.Path}:{l.Line}").Distinct());
                    effects.Add(left.Sections.FirstOrDefault(s => LinkText.Matches(s.Name, group.Key)) is { } now
                        ? $"{group.Count()} link(s) to '{group.Key}' by heading now reach the section '{now.Name}' at line {now.Line} instead: {where}."
                        : $"{group.Count()} link(s) to '{group.Key}' by heading no longer reach a section, only the note: {where}.");
                }

                return effects;
            });
        });

        static bool IsUnder(Section s, Section parent)
        {
            for (var p = s.Parent; p is not null; p = p.Parent)
            {
                if (p == parent)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Replaces the entity's whole file with <paramref name="text"/>. Its
    /// line breaks become the file's own (the more common one in a file that
    /// mixes them, F-ENC-3), and a byte order mark the file had is kept. An
    /// existing id can't be changed or removed (F-ID-4). The file stays where
    /// it is; a new name or alias doesn't rewrite the links that used the old
    /// text, and the result says so.
    /// </summary>
    public WriteResult ReplaceFile(string entity, string text, string expectedHash, string? universe = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Every id in the den, across universes, from the index (F-ID-3): DenWriteCheck reads only the file
        // being written (F-GEN-4), so it can't see an id another file has. The MCP tools never take an id by
        // hand (update_fields refuses one; new ids are made unused), so only Edit file needs this.
        var (_, ids) = Locate(entity, universe);
        return EditEntity(entity, universe, RequireHash(expectedHash), (file, catalog, pinned) =>
        {
            if (Lf(file.Text) == Lf(text))
            {
                // The same text, line breaks aside, writes nothing, even in a file that mixes them.
                return (file, (_, _) => []);
            }

            var newline = file.LineEndings == LineEndingKind.CrLf || (file.LineEndings == LineEndingKind.Mixed && CrLfMostly(file.Text)) ? "\r\n" : "\n";
            var written = LineBreak().Replace(text, newline);
            var payload = Encoding.UTF8.GetBytes(written);
            var bytes = file.HasByteOrderMark ? [0xEF, 0xBB, 0xBF, .. payload] : payload;
            var next = MarkdownFile.Parse(bytes, requireFrontmatter: true);
            var id = next.Frontmatter?["id"] is YamlScalar { IsNull: false } s ? s.Text : null;
            // Frontmatter that doesn't parse is refused with its errors, as the den reads it.
            if (catalog.Id is { } old && id != old && next.Frontmatter is not null)
            {
                throw new DenToolException(
                    ToolErrorCodes.UnsupportedEdit,
                    $"The note's id is {old}, and an id never changes (F-ID-4): links and history follow it. Put back 'id: {old}' and save again.");
            }

            if (catalog.Id is null && id is not null && ids.Contains(id))
            {
                throw new DenToolException(
                    ToolErrorCodes.AlreadyExists,
                    $"Another note or container already has the id {id}, and two files can't share one (F-ID-3): links to either would break. " +
                    "Take the id line out and save: the note gets a new id of its own (F-ID-6).");
            }

            return (next, (before, after) =>
            {
                pinned.AddRange(PinnedChanges(before, after));
                var touched = FieldNames(before).Union(FieldNames(after))
                    .Where(f => FieldJson(before, f) != FieldJson(after, f))
                    .ToList();
                var effects = new List<string> { "Saved the whole file." };
                effects.AddRange(FieldEffects(before, after, touched));
                if (!LinkText.Matches(before.Name, after.Name))
                {
                    effects.Add(
                        $"The name is now '{after.Name}'. Links written with the old name, '{before.Name}', weren't rewritten, so they no longer reach it " +
                        $"unless it's kept as an alias. {Words.NameLinks}");
                }

                var gone = before.Aliases.Where(a => !after.Aliases.Any(b => LinkText.Matches(a, b)) && !LinkText.Matches(a, after.Name)).ToList();
                if (gone.Count > 0)
                {
                    effects.Add(
                        $"Links written with the alias{(gone.Count == 1 ? "" : "es")} {string.Join(", ", gone.Select(a => $"'{a}'"))} weren't rewritten, " +
                        $"so they no longer reach it. {Words.AliasLinks}");
                }

                return effects;
            });
        });

        static bool CrLfMostly(string text)
        {
            var crlf = text.Split("\r\n").Length - 1;
            var lf = text.Count(c => c == '\n') - crlf;
            return crlf > lf;
        }

        static IEnumerable<string> FieldNames(CatalogEntity e) =>
            (e.File.Frontmatter?["fields"] as YamlMapping)?.Entries.Select(x => x.KeyText).OfType<string>() ?? [];

        static string FieldJson(CatalogEntity e, string name) => JsonOf((e.File.Frontmatter?["fields"] as YamlMapping)?[name]).GetRawText();
    }

    /// <summary>The section at <paramref name="index"/>, if it's named <paramref name="name"/>; otherwise <c>unknown_section</c>.</summary>
    private static Section SectionAt(MarkdownFile file, int index, string name)
    {
        if (index >= 0 && index < file.Sections.Count && file.Sections[index].Name == name)
        {
            return file.Sections[index];
        }

        throw new DenToolException(
            ToolErrorCodes.UnknownSection,
            index >= 0 && index < file.Sections.Count
                ? $"Section {index + 1} of the note is '{file.Sections[index].Name}' now, not '{name}'. Read the note again."
                : $"The note has no section {index + 1} now; it has {file.Sections.Count}. Read the note again.");
    }

    /// <summary>A section name, trimmed; refused if it's empty or more than one line.</summary>
    private static string SectionName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "A section name is one line of text, not empty.");
        }

        if (trimmed.StartsWith('#'))
        {
            // Refused rather than stripped: the level comes from where the section goes, and a name the
            // writer typed is never changed behind their back.
            throw new DenToolException(
                ToolErrorCodes.BadArgument, $"Give the section's name without the heading's '#' marks, such as '{trimmed.TrimStart('#').Trim()}'.");
        }

        return trimmed;
    }

    /// <summary>
    /// The pinned facts (format.md F-PIN-1) an edit changed, added, or
    /// removed, comparing the entity's facts before and after: each pinned
    /// section by its heading path and its place among sections of that
    /// path, and each pinned field entry by its field and place.
    /// </summary>
    private static List<string> PinnedChanges(CatalogEntity before, CatalogEntity after)
    {
        var was = PinnedFacts(before);
        var now = PinnedFacts(after);
        var changes = new List<string>();
        foreach (var (key, (what, content)) in was)
        {
            if (!now.TryGetValue(key, out var after2))
            {
                changes.Add($"{what} was pinned canon, and the edit removed or unpinned it");
            }
            else if (after2.Content != content)
            {
                changes.Add($"{what} is pinned canon, and the edit changed it");
            }
        }

        changes.AddRange(now.Where(p => !was.ContainsKey(p.Key)).Select(p => $"{p.Value.What} is pinned canon now"));
        return changes;
    }

    private static Dictionary<string, (string What, string Content)> PinnedFacts(CatalogEntity entity)
    {
        var facts = EntityFacts.For(entity, new Bookkeeping(entity.Universe.Den.BookkeepingSections));
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in facts.Sections)
        {
            var section = s.Section;
            var path = new List<string>();
            for (var p = section; p is not null; p = p.Parent)
            {
                path.Insert(0, LinkText.Normalize(p.Name));
            }

            var at = string.Join("\u001f", path);
            seen[at] = seen.GetValueOrDefault(at) + 1;
            if (s.Pinned)
            {
                result[$"s\u001f{at}\u001f{seen[at]}"] = ($"section '{section.Name}' (line {section.Line})", $"{section.Name}\n{section.Comment?.Text}\n{EditorText(section.Body)}");
            }
        }

        foreach (var f in facts.Fields)
        {
            foreach (var e in f.Entries.Where(e => e.Pinned))
            {
                var what = f.Entries.Count == 1 ? $"fields.{f.Name}" : $"fields.{f.Name}.{e.Index}";
                result[$"f\u001f{f.Name}\u001f{e.Index}"] = (what, JsonOf(e.Node).GetRawText());
            }
        }

        return result;
    }

    /// <summary>Text as an editor shows it: line breaks as LF, without the blank lines at either end.</summary>
    internal static string EditorText(string text)
    {
        var lines = Lf(text).Split('\n');
        int first = 0, last = lines.Length - 1;
        while (first <= last && lines[first].All(c => c is ' ' or '\t'))
        {
            first++;
        }

        while (last >= first && lines[last].All(c => c is ' ' or '\t'))
        {
            last--;
        }

        return first > last ? "" : string.Join('\n', lines[first..(last + 1)]);
    }

    private static string Lf(string text) => LineBreak().Replace(text, "\n");

    [GeneratedRegex("\r\n|\r|\n", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();
}
