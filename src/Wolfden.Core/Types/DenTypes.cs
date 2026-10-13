using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Types;

/// <summary>
/// A den's enabled packs and types (types.md §2 to §8): the built-in
/// <c>core</c> pack, or a den-local pack that shadows it, then the packs
/// <c>den.yaml</c> enables, combined in pack order. A broken pack file never
/// stops a den from loading: an error excludes that file, or the whole pack
/// for <c>pack.yaml</c> (F-PACK-2), and smaller problems drop only the item
/// involved, all reported in <see cref="Diagnostics"/>.
/// </summary>
public sealed class DenTypes
{
    /// <summary>
    /// What diagnostics put before a built-in pack file's path, such as
    /// <c>builtin:packs/core/types/thread.yaml</c>, so it can't be mistaken
    /// for a file in the den.
    /// </summary>
    public const string BuiltInPrefix = "builtin:";

    private static readonly Lazy<DenTypes> BuiltInOnly = new(() => Load(null, []));

    private readonly Dictionary<string, TypeDefinition> _types;
    private readonly Dictionary<string, FieldDefinition> _chapterFields;

    private DenTypes(List<PackDefinition> packs, Dictionary<string, TypeDefinition> types, List<FieldDefinition> chapterFields, List<DenDiagnostic> diagnostics)
    {
        Packs = packs;
        _types = types;
        Types = types.Values.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        ChapterFields = chapterFields;
        _chapterFields = chapterFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        Diagnostics = diagnostics
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Line ?? 0)
            .ToList();
    }

    /// <summary>The types of the built-in packs alone, as a den with no <c>packs/</c> folder and no <c>packs</c> list has them.</summary>
    public static DenTypes BuiltIn => BuiltInOnly.Value;

    /// <summary>The enabled packs in pack order (F-PACK-7), without those excluded for an error in <c>pack.yaml</c> (F-PACK-2).</summary>
    public IReadOnlyList<PackDefinition> Packs { get; }

    /// <summary>The enabled types (F-PACK-9), each its combined definition (F-TYPE-8), by key in ASCII order.</summary>
    public IReadOnlyList<TypeDefinition> Types { get; }

    /// <summary>The chapter fields of the enabled packs, in pack order (F-CHAP-2).</summary>
    public IReadOnlyList<FieldDefinition> ChapterFields { get; }

    /// <summary>Problems in pack files and between packs, by path and then line.</summary>
    public IReadOnlyList<DenDiagnostic> Diagnostics { get; }

    /// <summary>The enabled type with key <paramref name="key"/>, or null if it isn't one (an unknown type, F-ENT-2).</summary>
    public TypeDefinition? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _types.GetValueOrDefault(key);
    }

    /// <summary>The chapter field named <paramref name="name"/>, or null.</summary>
    public FieldDefinition? FindChapterField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _chapterFields.GetValueOrDefault(name);
    }

    /// <summary>
    /// Loads the types of a den: the built-in packs, the den-local packs in
    /// <paramref name="den"/>'s <c>packs/</c> folder, and the packs
    /// <paramref name="listedPacks"/> enables (from <c>den.yaml</c>'s
    /// <c>packs</c>, already checked against F-DEN-4). With a null
    /// <paramref name="den"/>, only built-in packs are read.
    /// </summary>
    public static DenTypes Load(IDenSource? den, IReadOnlyList<string> listedPacks) => Load(den, listedPacks, BuiltInPacks.Instance);

    /// <summary><see cref="Load(IDenSource?, IReadOnlyList{string})"/> with other built-in packs, for tests.</summary>
    internal static DenTypes Load(IDenSource? den, IReadOnlyList<string> listedPacks, BuiltInPacks builtIn)
    {
        ArgumentNullException.ThrowIfNull(listedPacks);
        ArgumentNullException.ThrowIfNull(builtIn);
        return new Builder(den, listedPacks, builtIn).Build();
    }

    /// <summary>Combines packs into types (types.md §2.3, §2.4, §3, §4, §8).</summary>
    private sealed class Builder(IDenSource? den, IReadOnlyList<string> listed, BuiltInPacks builtIn)
    {
        private readonly List<DenDiagnostic> _diagnostics = [];

        /// <summary>Built-in types that a den-local type replaced by taking their plural (F-TYPE-5), with that type's file.</summary>
        private readonly Dictionary<string, (string Key, string Path)> _replacedByDen = new(StringComparer.Ordinal);

        public DenTypes Build()
        {
            var packs = EnabledPacks();
            foreach (var pack in packs)
            {
                _diagnostics.AddRange(pack.Diagnostics);
            }

            var enabled = packs.Where(p => !p.Excluded).ToList();
            var types = CombineTypes(enabled);
            var chapterFields = CombineChapterFields(enabled);
            CheckRefTargets(types.Values.SelectMany(t => t.Fields).Concat(chapterFields), types);
            foreach (var type in types.Values)
            {
                CheckTemplate(type);
            }

            return new DenTypes(enabled.Select(p => p.Pack).ToList(), types, chapterFields, _diagnostics);
        }

        /// <summary>F-PACK-5, F-PACK-7, F-PACK-8, F-PACK-10: the packs that are enabled, in pack order.</summary>
        private List<PackContent> EnabledPacks()
        {
            var local = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (den is not null)
            {
                foreach (var folder in den.GetDirectories("packs").Where(n => !n.StartsWith('.')).Order(StringComparer.Ordinal))
                {
                    var path = $"packs/{folder}";
                    if (!Keys.IsKey(folder))
                    {
                        Warn(path, "F-PACK-5", null, $"'{folder}' isn't a valid pack key (lowercase letters and digits, with single hyphens between words), so the folder is ignored.");
                    }
                    else if (!den.FileExists($"{path}/pack.yaml"))
                    {
                        Warn(path, "F-PACK-5", null, den.FileExists($"{path}/pack.yml")
                            ? $"packs/{folder} has pack.yml, which isn't read: rename it to pack.yaml to make the folder a pack."
                            : $"packs/{folder} has no pack.yaml, so it isn't a pack.");
                    }
                    else
                    {
                        local[folder] = path;
                    }
                }
            }

            foreach (var key in local.Keys.Where(k => builtIn.Keys.Contains(k)))
            {
                Warn($"packs/{key}/pack.yaml", "F-PACK-10", null,
                    $"packs/{key} shadows the built-in pack '{key}', so Wolfden's updates to {key} won't reach this den; rename the folder to make it an ordinary pack.");
            }

            PackContent Read(string key)
            {
                if (local.TryGetValue(key, out var path))
                {
                    var pack = new PackDefinition(key, isBuiltIn: false, path) { Shadows = builtIn.Keys.Contains(key) };
                    return PackReader.Read(den!, path, pack);
                }

                return PackReader.Read(builtIn, key, new PackDefinition(key, isBuiltIn: true, $"{BuiltInPrefix}packs/{key}"));
            }

            var result = new List<PackContent> { Read("core") };
            var keys = listed.Where(k => k != "core").Distinct(StringComparer.Ordinal).ToList();
            result.AddRange(keys.Where(k => !local.ContainsKey(k) && builtIn.Keys.Contains(k)).Select(Read));
            result.AddRange(keys.Where(local.ContainsKey).Select(Read));
            foreach (var key in local.Keys.Where(k => k != "core" && !keys.Contains(k)))
            {
                Warn($"packs/{key}/pack.yaml", "F-PACK-8", null, $"pack '{key}' is in packs/ but not enabled in den.yaml, so its types are unknown types until den.yaml lists it.");
            }

            return result;
        }

        /// <summary>F-TYPE-4, F-TYPE-5, F-TYPE-7, F-TYPE-8: each enabled type's combined definition.</summary>
        private Dictionary<string, TypeDefinition> CombineTypes(List<PackContent> packs)
        {
            // F-TYPE-4: one definition per type key.
            var chosen = new Dictionary<string, (int Order, PackContent Pack, TypeFile File)>(StringComparer.Ordinal);
            for (var i = 0; i < packs.Count; i++)
            {
                foreach (var file in packs[i].Types)
                {
                    if (!chosen.TryGetValue(file.Key, out var existing))
                    {
                        chosen[file.Key] = (i, packs[i], file);
                        continue;
                    }

                    var candidate = (Order: i, Pack: packs[i], File: file);
                    var newWins = Wins(packs[i].Pack, existing.Pack.Pack);
                    var (used, ignored) = newWins ? (candidate, existing) : (existing, candidate);
                    Clash("F-TYPE-4", $"type '{file.Key}'", used.Pack.Pack, used.File.Path, null, ignored.Pack.Pack, ignored.File.Path, null);
                    chosen[file.Key] = used;
                }
            }

            // F-TYPE-5: no two types share a plural. Within a pack, the earlier key wins.
            var byPlural = new Dictionary<string, (int Order, PackContent Pack, TypeFile File)>(StringComparer.Ordinal);
            foreach (var candidate in chosen.Values.OrderBy(c => c.Order).ThenBy(c => c.File.Key, StringComparer.Ordinal).ToList())
            {
                if (!byPlural.TryGetValue(candidate.File.Plural, out var existing))
                {
                    byPlural[candidate.File.Plural] = candidate;
                    continue;
                }

                var newWins = existing.Pack != candidate.Pack && Wins(candidate.Pack.Pack, existing.Pack.Pack);
                var (used, ignored) = newWins ? (candidate, existing) : (existing, candidate);
                Clash("F-TYPE-5", $"the plural '{candidate.File.Plural}' (types '{used.File.Key}' and '{ignored.File.Key}')",
                    used.Pack.Pack, used.File.Path, null, ignored.Pack.Pack, ignored.File.Path, null,
                    $"; type '{ignored.File.Key}' is ignored");
                byPlural[candidate.File.Plural] = used;
                chosen.Remove(ignored.File.Key);
                if (ignored.Pack.Pack.IsBuiltIn && !used.Pack.Pack.IsBuiltIn)
                {
                    _replacedByDen[ignored.File.Key] = (used.File.Key, used.File.Path);
                }
            }

            var result = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            foreach (var (key, (_, pack, file)) in chosen)
            {
                result[key] = Combine(pack, file, packs);
            }

            foreach (var pack in packs)
            {
                foreach (var extension in pack.Extensions.Where(e => !chosen.ContainsKey(e.Key)))
                {
                    Warn(extension.Path, "F-TYPE-7", null, $"There's no enabled type '{extension.Key}', so this extension is ignored.");
                }
            }

            return result;
        }

        /// <summary>F-TYPE-8: a type's own definition, then each enabled extension of it in pack order.</summary>
        private TypeDefinition Combine(PackContent owner, TypeFile file, List<PackContent> packs)
        {
            var fields = new List<FieldDefinition>(file.Fields);
            var sections = new List<SectionDefinition>(file.Sections);
            var extensionTemplates = new List<(PackContent Pack, ExtensionFile File)>();
            foreach (var pack in packs)
            {
                foreach (var extension in pack.Extensions.Where(e => e.Key == file.Key))
                {
                    foreach (var field in extension.Fields)
                    {
                        var index = fields.FindIndex(f => f.Name == field.Name);
                        if (index < 0)
                        {
                            fields.Add(field);
                            continue;
                        }

                        var existing = fields[index];
                        var newWins = !field.Pack.IsBuiltIn && existing.Pack.IsBuiltIn;
                        var (used, ignored) = newWins ? (field, existing) : (existing, field);
                        Clash("F-TYPE-8", $"field '{field.Name}' of type '{file.Key}'", used.Pack, used.Path, used.Line, ignored.Pack, ignored.Path, ignored.Line);
                        if (newWins)
                        {
                            fields.RemoveAt(index);
                            fields.Add(field);
                        }
                    }

                    foreach (var section in extension.Sections)
                    {
                        var index = sections.FindIndex(s => s.NormalizedName == section.NormalizedName);
                        if (index < 0)
                        {
                            sections.Add(section);
                            continue;
                        }

                        var existing = sections[index];
                        var newWins = !section.Pack.IsBuiltIn && existing.Pack.IsBuiltIn;
                        var (used, ignored) = newWins ? (section, existing) : (existing, section);
                        Clash("F-TYPE-8", $"section '{section.Name}' of type '{file.Key}'", used.Pack, used.Path, used.Line, ignored.Pack, ignored.Path, ignored.Line);
                        if (newWins)
                        {
                            sections.RemoveAt(index);
                            sections.Add(section);
                        }
                    }

                    if (extension.Template is not null)
                    {
                        extensionTemplates.Add((pack, extension));
                    }
                }
            }

            // The type's own template: by name, or templates/<key>.md (F-TPL-2).
            var template = file.Template is { } own ? owner.Templates[own] : owner.Templates.GetValueOrDefault($"{file.Key}.md");
            var templatePack = owner.Pack;

            // F-TYPE-8: of the extensions that set a template, F-PACK-11 picks one.
            var winner = extensionTemplates.FirstOrDefault(e => !e.Pack.Pack.IsBuiltIn);
            if (winner.Pack is null)
            {
                winner = extensionTemplates.FirstOrDefault();
            }

            foreach (var other in extensionTemplates.Where(e => e.Pack != winner.Pack))
            {
                Clash("F-TYPE-8", $"a template for type '{file.Key}'", winner.Pack.Pack, winner.File.Path, null, other.Pack.Pack, other.File.Path, null);
            }

            if (winner.Pack is not null)
            {
                if (winner.Pack.Pack.IsBuiltIn && !owner.Pack.IsBuiltIn)
                {
                    // A built-in extension's template never replaces a den-local type's own.
                    Clash("F-TYPE-8", $"the template of type '{file.Key}'", owner.Pack, file.Path, null, winner.Pack.Pack, winner.File.Path, null);
                }
                else
                {
                    template = winner.Pack.Templates[winner.File.Template!];
                    templatePack = winner.Pack.Pack;
                }
            }

            var definition = template is { Excluded: false }
                ? new TemplateDefinition(templatePack, template.FileName, template.Path, template.File)
                : Generated(file.Path, sections);
            return new TypeDefinition(file.Key, owner.Pack, file.Path, file.Name, file.Plural, file.Description, fields, sections, definition);
        }

        /// <summary>F-CHAP-2: the chapter fields of every enabled pack, in pack order.</summary>
        private List<FieldDefinition> CombineChapterFields(List<PackContent> packs)
        {
            var fields = new List<FieldDefinition>();
            foreach (var pack in packs)
            {
                foreach (var field in pack.Chapter?.Fields ?? [])
                {
                    var index = fields.FindIndex(f => f.Name == field.Name);
                    if (index < 0)
                    {
                        fields.Add(field);
                        continue;
                    }

                    var existing = fields[index];
                    var newWins = Wins(field.Pack, existing.Pack);
                    var (used, ignored) = newWins ? (field, existing) : (existing, field);
                    Clash("F-CHAP-2", $"chapter field '{field.Name}'", used.Pack, used.Path, used.Line, ignored.Pack, ignored.Path, ignored.Line);
                    if (newWins)
                    {
                        fields.RemoveAt(index);
                        fields.Add(field);
                    }
                }
            }

            return fields;
        }

        /// <summary>F-FT-9: a <c>to</c> naming a type that isn't enabled is a warning, and the type check skips that name.</summary>
        private void CheckRefTargets(IEnumerable<FieldDefinition> fields, Dictionary<string, TypeDefinition> types)
        {
            // A built-in field whose target a den-local type replaced is reported
            // once, on the den's own file, rather than on files the writer can't edit.
            var redirected = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var field in fields.Where(f => f.To is not null))
            {
                var known = new List<string>();
                foreach (var key in field.To!)
                {
                    if (types.ContainsKey(key))
                    {
                        known.Add(key);
                    }
                    else if (field.Pack.IsBuiltIn && _replacedByDen.ContainsKey(key))
                    {
                        if (!redirected.TryGetValue(key, out var names))
                        {
                            redirected[key] = names = [];
                        }

                        names.Add(field.Name);
                    }
                    else
                    {
                        Warn(field.Path, "F-FT-9", field.Line, $"'{field.Name}' links to type '{key}', which isn't an enabled type, so the type check skips it.");
                    }
                }

                // With every name skipped, nothing is left to check against.
                field.CheckedTo = known;
            }

            foreach (var (key, names) in redirected)
            {
                var (by, path) = _replacedByDen[key];
                Warn(path, "F-FT-9", null,
                    $"Type '{by}' replaces the built-in type '{key}', so the built-in fields that link to '{key}' ({string.Join(", ", names.Distinct(StringComparer.Ordinal))}) skip that type check.");
            }
        }

        /// <summary>The checks of F-TPL-3 that depend on the type using the template: its <c>type</c> and its fields.</summary>
        private void CheckTemplate(TypeDefinition type)
        {
            var template = type.Template;
            if (template.IsGenerated || template.File.Frontmatter is not { } fm)
            {
                return;
            }

            if (fm["type"] is YamlScalar { IsNull: false } written && written.Text != type.Key)
            {
                Warn(template.Path, "F-TPL-3", written.Line, $"This template is used by type '{type.Key}', so its type '{written.Text}' is ignored and '{type.Key}' is used.");
            }

            if (fm["fields"] is YamlMapping fields)
            {
                foreach (var entry in fields.Entries)
                {
                    if (entry.KeyText is { } name && type.FindField(name) is null && !TypeDefinition.IsUniversalField(name))
                    {
                        Warn(template.Path, "F-FLD-2", entry.Key.Line, $"Type '{type.Key}' doesn't define the field '{name}'.");
                    }
                }
            }
        }

        /// <summary>F-TPL-2: <c>status: draft</c>, then <c># {{name}}</c> and a level-2 heading for each standard section.</summary>
        private static TemplateDefinition Generated(string typePath, List<SectionDefinition> sections)
        {
            var text = new StringBuilder("---\nstatus: draft\n---\n\n# {{name}}\n\n");
            foreach (var section in sections)
            {
                text.Append("## ").Append(section.Name).Append("\n\n");
            }

            return new TemplateDefinition(null, null, typePath, MarkdownFile.Parse(Encoding.UTF8.GetBytes(text.ToString())));
        }

        /// <summary>F-PACK-11: <paramref name="later"/> wins over <paramref name="earlier"/> only if it's den-local and the other is built in.</summary>
        private static bool Wins(PackDefinition later, PackDefinition earlier) => !later.IsBuiltIn && earlier.IsBuiltIn;

        /// <summary>
        /// A clash warning (F-PACK-11): it names both packs and says which
        /// definition is used. It goes on the file whose definition is
        /// ignored, or, when that file is built in, on the den-local file
        /// whose definition is used.
        /// </summary>
        private void Clash(string rule, string what, PackDefinition used, string usedPath, int? usedLine,
            PackDefinition ignored, string ignoredPath, int? ignoredLine, string extra = "")
        {
            var (path, line) = ignored.IsBuiltIn && !used.IsBuiltIn ? (usedPath, usedLine) : (ignoredPath, ignoredLine);
            var message = used == ignored
                ? $"{what} is defined twice in {used}; using the one in {usedPath}{extra}."
                : $"{what} is defined by both {used} and {ignored}; using '{used.Key}'{extra}.";
            Warn(path, rule, line, message);
        }

        private void Warn(string path, string rule, int? line, string message) =>
            _diagnostics.Add(new DenDiagnostic(path, new Diagnostic(rule, DiagnosticSeverity.Warning, line, message)));
    }
}
