using System.Text.RegularExpressions;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Types;

/// <summary>A type file that loaded (types.md F-TYPE-1, F-TYPE-3).</summary>
internal sealed class TypeFile(string key, string path, string name, string plural, string? description,
    List<FieldDefinition> fields, List<SectionDefinition> sections, string? template)
{
    public string Key => key;

    public string Path => path;

    public string Name => name;

    public string Plural => plural;

    public string? Description => description;

    public List<FieldDefinition> Fields => fields;

    public List<SectionDefinition> Sections => sections;

    /// <summary>The <c>template</c> value, if it's valid (F-TPL-1).</summary>
    public string? Template => template;
}

/// <summary>An extension file that loaded (types.md F-TYPE-6).</summary>
internal sealed record ExtensionFile(string Key, string Path, List<FieldDefinition> Fields, List<SectionDefinition> Sections, string? Template);

/// <summary>A template file in a pack's <c>templates/</c>, checked as F-TPL-3 says.</summary>
/// <param name="Excluded">True if it has an error, so types that would use it use the generated template.</param>
internal sealed record TemplateFile(string FileName, string Path, MarkdownFile File, bool Excluded);

/// <summary>What one pack folder holds, read and checked on its own (types.md §2 to §8).</summary>
internal sealed class PackContent(PackDefinition pack)
{
    public PackDefinition Pack => pack;

    /// <summary>True if <c>pack.yaml</c> has an error, so none of the pack's files load (F-PACK-2).</summary>
    public bool Excluded { get; set; }

    public List<TypeFile> Types { get; } = [];

    public List<ExtensionFile> Extensions { get; } = [];

    /// <summary>The chapter fields from <c>chapter.yaml</c>, with its path, or null.</summary>
    public (string Path, List<FieldDefinition> Fields)? Chapter { get; set; }

    public Dictionary<string, TemplateFile> Templates { get; } = new(StringComparer.Ordinal);

    public List<DenDiagnostic> Diagnostics { get; } = [];
}

/// <summary>
/// Reads one pack folder: <c>pack.yaml</c>, <c>chapter.yaml</c>, type files,
/// extension files, and templates. Everything here depends only on the
/// pack's own files; combining packs is <see cref="DenTypes"/>'s job.
/// </summary>
internal sealed partial class PackReader
{
    /// <summary>Type keys no pack may define (F-TYPE-2).</summary>
    internal static readonly IReadOnlySet<string> ReservedTypeKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "universe", "series", "book", "chapter", "scene", "outline", "den", "author", "pack",
    };

    /// <summary>Field names no chapter field may have (F-CHAP-1).</summary>
    internal static readonly IReadOnlySet<string> ReservedChapterFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "key", "number", "title", "id", "name", "status", "from", "until", "pin", "fields", "row", "placeholder", "scene",
    };

    private readonly IDenSource _source;
    private readonly string _folder;
    private readonly PackContent _content;

    private PackReader(IDenSource source, string folder, PackDefinition pack)
    {
        _source = source;
        _folder = folder;
        _content = new PackContent(pack);
    }

    private PackDefinition Pack => _content.Pack;

    /// <summary>
    /// Reads the pack in <paramref name="folder"/> of <paramref name="source"/>.
    /// <paramref name="pack"/> says how diagnostics name its files.
    /// </summary>
    public static PackContent Read(IDenSource source, string folder, PackDefinition pack)
    {
        var reader = new PackReader(source, folder, pack);
        reader.Run();
        return reader._content;
    }

    private void Run()
    {
        if (!ReadManifest())
        {
            _content.Excluded = true;
            return;
        }

        foreach (var name in Files(""))
        {
            if (name is "pack.yml" or "chapter.yml")
            {
                Warn(name, "F-PACK-3", null, $"{name} isn't read: did you mean {name[..^4]}.yaml?");
            }
        }

        foreach (var name in Files("templates").Where(n => n.EndsWith(".md", StringComparison.Ordinal)))
        {
            ReadTemplate(name);
        }

        // A chapter.yaml that's listed but can't be read leaves no chapter
        // fields, as if it weren't there: ReadChapterFile returns early (#112).
        if (_source.FileExists(Source("chapter.yaml")))
        {
            ReadChapterFile();
        }

        // A type or extension file that's gone or can't be read is left out
        // entirely, as if it weren't listed, template use included (#112).
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in YamlFiles("types"))
        {
            if (Yaml($"types/{name}", listed: true) is not { } file)
            {
                continue;
            }

            if (ReadType(name[..^5], file) is { } type)
            {
                _content.Types.Add(type);
            }

            // F-TPL-2: a type uses its template by name, or templates/<key>.md.
            used.Add(TemplateValue(file) ?? name[..^5] + ".md");
        }

        foreach (var name in YamlFiles("extensions"))
        {
            if (Yaml($"extensions/{name}", listed: true) is not { } file)
            {
                continue;
            }

            if (ReadExtension(name[..^5], file) is { } extension)
            {
                _content.Extensions.Add(extension);
            }

            if (TemplateValue(file) is { } template)
            {
                used.Add(template);
            }
        }

        foreach (var template in _content.Templates.Values.Where(t => !used.Contains(t.FileName)))
        {
            var key = template.FileName[..^3];
            Warn($"templates/{template.FileName}", "F-TPL-2", null,
                $"template {template.FileName} isn't used; add `template: {template.FileName}` to types/{key}.yaml or extensions/{key}.yaml.");
        }
    }

    /// <summary>F-PACK-6. Returns false if <c>pack.yaml</c> has an error, which excludes the pack (F-PACK-2).</summary>
    private bool ReadManifest()
    {
        const string name = "pack.yaml";
        if (Yaml(name) is not { } file)
        {
            // Only if it went between the den reading it to call this a pack
            // and now (#112): the next load sees the folder isn't a pack.
            return false;
        }

        var ok = !file.HasErrors;
        var root = file.Root;
        if (ok && root is null)
        {
            Error(name, "F-PACK-6", 1, "pack.yaml must be a mapping with at least a name, such as 'name: Starfall'.");
            return false;
        }

        if (!ok)
        {
            return false;
        }

        if (root!["name"] is YamlScalar { IsNull: false } n && n.Text.Trim().Length > 0)
        {
            Pack.Name = n.Text;
        }
        else
        {
            Error(name, "F-PACK-6", root["name"]?.Line ?? 1, "A pack needs a non-empty name, such as 'name: Starfall'.");
            ok = false;
        }

        foreach (var key in new[] { "description", "version" })
        {
            switch (root[key])
            {
                case null or YamlScalar { IsNull: true }:
                    break;
                case YamlScalar s:
                    if (key == "description")
                    {
                        Pack.Description = s.Text;
                    }
                    else
                    {
                        Pack.Version = s.Text;
                    }

                    break;
                case var node:
                    Error(name, "F-PACK-6", node.Line, $"A pack's {key} must be a string.");
                    ok = false;
                    break;
            }
        }

        return ok;
    }

    private void ReadChapterFile()
    {
        const string name = "chapter.yaml";
        var file = Yaml(name);
        if (file is null || file.HasErrors)
        {
            return;
        }

        if (file.Root is not { } root)
        {
            // An empty document defines no chapter fields.
            _content.Chapter = (Path(name), []);
            return;
        }

        if (root["fields"] is { } f && f is not (YamlMapping or YamlScalar { IsNull: true }))
        {
            Error(name, "F-CHAP-1", f.Line, "chapter.yaml's fields must be a mapping from field name to field definition.");
            return;
        }

        foreach (var entry in root.Entries.Where(e => e.KeyText != "fields"))
        {
            Warn(name, "F-CHAP-1", entry.Key.Line, $"chapter.yaml has only fields, not '{entry.KeyText}'. The key is kept and ignored.");
        }

        var fields = root["fields"] is YamlMapping map ? ReadFields(name, map, chapter: true) : [];
        _content.Chapter = (Path(name), fields);
    }

    /// <summary>F-TYPE-1 to F-TYPE-3. Returns null if the file has an error, which excludes it (F-PACK-2).</summary>
    private TypeFile? ReadType(string key, YamlFile file)
    {
        var name = $"types/{key}.yaml";
        if (file.HasErrors)
        {
            return null;
        }

        if (!Keys.IsKey(key))
        {
            Error(name, "F-TYPE-1", null, $"'{key}' isn't a valid type key: name the file with 1 to 64 lowercase letters and digits, with single hyphens between words.");
            return null;
        }

        if (ReservedTypeKeys.Contains(key))
        {
            Error(name, "F-TYPE-2", null, $"'{key}' is reserved and can't be a type key. Name the type something else.");
            return null;
        }

        if (file.Root is not { } root)
        {
            Error(name, "F-TYPE-3", 1, "A type file must be a mapping with at least a name and a plural.");
            return null;
        }

        var ok = true;
        string? typeName = null;
        if (root["name"] is YamlScalar { IsNull: false } n && n.Text.Trim().Length > 0)
        {
            typeName = n.Text;
        }
        else
        {
            Error(name, "F-TYPE-3", root["name"]?.Line ?? 1, "A type needs a non-empty name, such as 'name: Ship'.");
            ok = false;
        }

        string? plural = null;
        if (root["plural"] is YamlScalar { IsNull: false } p && Keys.IsKey(p.Text))
        {
            plural = p.Text;
        }
        else
        {
            Error(name, "F-TYPE-3", root["plural"]?.Line ?? 1,
                "A type needs a plural: its folder name under entities/, a key such as 'plural: ships' (lowercase letters and digits, with single hyphens between words).");
            ok = false;
        }

        string? description = null;
        switch (root["description"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar d:
                description = d.Text;
                break;
            case var node:
                Error(name, "F-TYPE-3", node.Line, "A type's description must be a string.");
                ok = false;
                break;
        }

        ok &= CheckShapes(name, root, "F-TYPE-3");
        foreach (var entry in root.Entries.Where(e => e.KeyText is not ("name" or "plural" or "description" or "fields" or "sections" or "template")))
        {
            Warn(name, "F-TYPE-3", entry.Key.Line, $"A type file has name, plural, description, fields, sections, and template, not '{entry.KeyText}'. The key is kept and ignored.");
        }

        if (!ok)
        {
            return null;
        }

        return new TypeFile(key, Path(name), typeName!, plural!, description,
            root["fields"] is YamlMapping fields ? ReadFields(name, fields, chapter: false) : [],
            root["sections"] is YamlSequence sections ? ReadSections(name, sections) : [],
            CheckTemplate(name, root["template"]));
    }

    /// <summary>F-TYPE-6. Returns null if the file has an error, which excludes it (F-PACK-2).</summary>
    private ExtensionFile? ReadExtension(string key, YamlFile file)
    {
        var name = $"extensions/{key}.yaml";
        if (file.HasErrors)
        {
            return null;
        }

        if (!Keys.IsKey(key))
        {
            Error(name, "F-TYPE-6", null, $"'{key}' isn't a valid type key: name an extension file after the type it extends.");
            return null;
        }

        if (file.Root is not { } root)
        {
            // An empty document (a top level that isn't a mapping is already
            // an F-YAML-2 error): the extension adds nothing.
            return new ExtensionFile(key, Path(name), [], [], null);
        }

        var ok = CheckShapes(name, root, "F-TYPE-6");
        foreach (var entry in root.Entries)
        {
            switch (entry.KeyText)
            {
                case "fields" or "sections" or "template":
                    break;
                case "name" or "plural" or "description":
                    Error(name, "F-TYPE-6", entry.Key.Line, $"An extension can't set {entry.KeyText}: it can't rename a type or move its folder.");
                    ok = false;
                    break;
                default:
                    Warn(name, "F-TYPE-6", entry.Key.Line, $"An extension file has fields, sections, and template, not '{entry.KeyText}'. The key is kept and ignored.");
                    break;
            }
        }

        if (!ok)
        {
            return null;
        }

        return new ExtensionFile(key, Path(name),
            root["fields"] is YamlMapping fields ? ReadFields(name, fields, chapter: false) : [],
            root["sections"] is YamlSequence sections ? ReadSections(name, sections) : [],
            CheckTemplate(name, root["template"]));
    }

    /// <summary>The shapes of <c>fields</c> (a mapping) and <c>sections</c> (a list), errors in a type or extension file.</summary>
    private bool CheckShapes(string name, YamlMapping root, string rule)
    {
        var ok = true;
        if (root["fields"] is { } fields && fields is not (YamlMapping or YamlScalar { IsNull: true }))
        {
            Error(name, rule, fields.Line, "fields must be a mapping from field name to field definition.");
            ok = false;
        }

        if (root["sections"] is { } sections && sections is not (YamlSequence or YamlScalar { IsNull: true }))
        {
            Error(name, rule, sections.Line, "sections must be a list of section names, such as '[Overview, Crew]'.");
            ok = false;
        }

        return ok;
    }

    /// <summary>F-FT-1, F-FT-2, F-FT-8, F-FT-14, F-FT-15, and for chapter fields F-CHAP-1.</summary>
    private List<FieldDefinition> ReadFields(string file, YamlMapping fields, bool chapter)
    {
        var result = new List<FieldDefinition>();
        foreach (var entry in fields.Entries)
        {
            var fieldName = entry.KeyText;
            var line = entry.Key.Line;
            if (fieldName is null || !FieldName().IsMatch(fieldName))
            {
                Warn(file, "F-FT-1", line, $"'{fieldName}' isn't a valid field name: use lowercase letters, digits, and '_', starting with a letter, up to 64 characters. The definition is ignored.");
                continue;
            }

            if (chapter && ReservedChapterFields.Contains(fieldName))
            {
                Warn(file, "F-CHAP-1", line, $"'{fieldName}' is reserved for a chapter item's own keys, so it can't be a chapter field. The definition is ignored.");
                continue;
            }

            if (!chapter && TypeDefinition.IsUniversalField(fieldName))
            {
                Warn(file, "F-FT-1", line, $"'{fieldName}' is defined on every type, so no pack may define it. The definition is ignored.");
                continue;
            }

            if (ReadField(file, fieldName, line, entry.Value, chapter) is { } field)
            {
                result.Add(field);
            }
        }

        return result;
    }

    private FieldDefinition? ReadField(string file, string name, int line, YamlNode value, bool chapter)
    {
        string Broken(string why) => $"The definition of '{name}' is ignored: {why}";

        if (value is YamlScalar { IsNull: false } shorthand)
        {
            if (FieldKinds.Parse(shorthand.Text) is not { } shortKind)
            {
                Warn(file, "F-FT-2", shorthand.Line, Broken($"'{shorthand.Text}' isn't a field type. Use string, text, number, bool, date, enum, ref, or position."));
                return null;
            }

            if (shortKind == FieldKind.Enum)
            {
                Warn(file, "F-FT-8", shorthand.Line, Broken("an enum needs values, such as '{ type: enum, values: [past, present] }'."));
                return null;
            }

            return new FieldDefinition(name, shortKind, Pack, Path(file), line);
        }

        if (value is not YamlMapping map)
        {
            Warn(file, "F-FT-2", value.Line, Broken("a field definition is a field type, such as 'date', or a mapping with a type."));
            return null;
        }

        if (map["type"] is not YamlScalar { IsNull: false } typeNode || FieldKinds.Parse(typeNode.Text) is not { } kind)
        {
            var at = map["type"];
            Warn(file, "F-FT-2", at?.Line ?? value.Line, Broken(at is YamlScalar { IsNull: false } t
                ? $"'{t.Text}' isn't a field type. Use string, text, number, bool, date, enum, ref, or position."
                : "it has no type."));
            return null;
        }

        var field = new FieldDefinition(name, kind, Pack, Path(file), line);
        var broken = false;
        YamlNode? defaultNode = null;
        foreach (var entry in map.Entries)
        {
            var key = entry.KeyText;
            var node = entry.Value;
            if (chapter && key is "temporal" or "required" or "default")
            {
                Warn(file, "F-CHAP-1", entry.Key.Line, $"A chapter field has no {key}, so it's ignored.");
                continue;
            }

            switch (key)
            {
                case "type":
                    break;
                case "list" or "temporal" or "required":
                    if (node is YamlScalar { IsNull: true })
                    {
                        break;
                    }

                    if (node is not YamlScalar flag || flag.AsBoolean() is not bool on)
                    {
                        Warn(file, "F-FT-2", node.Line, Broken($"{key} must be true or false."));
                        broken = true;
                        break;
                    }

                    if (key == "list")
                    {
                        field.IsList = on;
                    }
                    else if (key == "temporal")
                    {
                        field.IsTemporal = on;
                    }
                    else
                    {
                        field.IsRequired = on;
                    }

                    break;
                case "values":
                    if (kind != FieldKind.Enum)
                    {
                        Warn(file, "F-FT-2", entry.Key.Line, $"values is only for an enum, so it's ignored on '{name}'.");
                        break;
                    }

                    if (ReadValues(file, name, node) is { } values)
                    {
                        field.Values = values;
                    }
                    else
                    {
                        broken = true;
                    }

                    break;
                case "to":
                    if (kind != FieldKind.Ref)
                    {
                        Warn(file, "F-FT-2", entry.Key.Line, $"to is only for a ref, so it's ignored on '{name}'.");
                        break;
                    }

                    var to = node switch
                    {
                        YamlScalar { IsNull: true } => [],
                        YamlScalar s when Keys.IsKey(s.Text) => [s.Text],
                        YamlSequence { Items.Count: > 0 } list when list.Items.All(i => i is YamlScalar { IsNull: false } s && Keys.IsKey(s.Text)) =>
                            list.Items.Cast<YamlScalar>().Select(s => s.Text).Distinct(StringComparer.Ordinal).ToList(),
                        _ => (List<string>?)null,
                    };

                    if (to is null)
                    {
                        Warn(file, "F-FT-2", node.Line, Broken("to must be a type key or a list of type keys, such as 'to: [character, faction]'."));
                        broken = true;
                    }
                    else if (to.Count > 0)
                    {
                        field.To = to;
                    }

                    break;
                case "description":
                    if (node is YamlScalar { IsNull: false } d)
                    {
                        field.Description = d.Text;
                    }
                    else if (node is not YamlScalar { IsNull: true })
                    {
                        Warn(file, "F-FT-2", node.Line, $"The description of '{name}' must be a string, so it's ignored.");
                    }

                    break;
                case "default":
                    defaultNode = node is YamlScalar { IsNull: true } ? null : node;
                    break;
                default:
                    Warn(file, "F-FT-2", entry.Key.Line, $"A field definition has type, list, temporal, required, default, values, to, and description, not '{key}'. The key is kept and ignored.");
                    break;
            }
        }

        if (kind == FieldKind.Enum && !broken && field.Values.Count == 0)
        {
            Warn(file, "F-FT-8", value.Line, Broken("an enum needs values, a non-empty list of keys."));
            broken = true;
        }

        if (broken)
        {
            return null;
        }

        if (defaultNode is not null)
        {
            if (kind is FieldKind.Ref or FieldKind.Position)
            {
                Warn(file, "F-FT-15", defaultNode.Line, $"A {kind.ToKey()} field has no default, so the default of '{name}' is ignored.");
            }
            else
            {
                // Advisory problems (a date's calendar, F-FT-11) are their own
                // warnings and don't make the default a mismatch.
                var check = FieldValues.Check(field, defaultNode);
                foreach (var advisory in check.Problems.Where(p => p.IsAdvisory))
                {
                    Warn(file, advisory.RuleId, advisory.Line, $"In the default of '{name}': {advisory.Message}");
                }

                if (check.Problems.FirstOrDefault(p => !p.IsAdvisory) is { } reason)
                {
                    Warn(file, "F-FT-15", defaultNode.Line, $"The default of '{name}' is ignored: {reason.Message}");
                }
                else
                {
                    field.Default = defaultNode;
                }
            }
        }

        if (field.IsRequired && map["default"] is { } d2 && d2 is not YamlScalar { IsNull: true })
        {
            Warn(file, "F-FT-14", line, $"'{name}' has both required and a default, but a default never makes a field present.");
        }

        return field;
    }

    /// <summary>F-FT-8: an enum's values. Null if the definition is broken.</summary>
    private List<string>? ReadValues(string file, string name, YamlNode node)
    {
        if (node is not YamlSequence list)
        {
            Warn(file, "F-FT-8", node.Line, $"The definition of '{name}' is ignored: values must be a non-empty list of keys, such as '[past, present]'.");
            return null;
        }

        var values = new List<string>();
        foreach (var item in list.Items)
        {
            if (item is not YamlScalar { IsNull: false } scalar)
            {
                Warn(file, "F-FT-8", item.Line, $"Each value of '{name}' must be a key. This one is dropped.");
                continue;
            }

            var value = scalar.Text;
            if (!Keys.IsKey(value))
            {
                var lower = value.ToLowerInvariant();
                if (!Keys.IsKey(lower))
                {
                    Warn(file, "F-FT-8", item.Line, $"'{value}' isn't a key (lowercase letters and digits, with single hyphens between words), so it's dropped from the values of '{name}'.");
                    continue;
                }

                Warn(file, "F-FT-8", item.Line, $"'{value}' is read as '{lower}': values are lowercase keys.");
                value = lower;
            }

            if (values.Contains(value, StringComparer.Ordinal))
            {
                Warn(file, "F-FT-8", item.Line, $"'{value}' is repeated in the values of '{name}', so the repeat is dropped.");
                continue;
            }

            values.Add(value);
        }

        if (values.Count == 0)
        {
            Warn(file, "F-FT-8", node.Line, $"The definition of '{name}' is ignored: it has no valid values.");
            return null;
        }

        return values;
    }

    /// <summary>F-TYPE-9: the standard sections.</summary>
    private List<SectionDefinition> ReadSections(string file, YamlSequence sections)
    {
        var result = new List<SectionDefinition>();
        foreach (var item in sections.Items)
        {
            SectionDefinition? section = null;
            if (item is YamlScalar { IsNull: false } s && s.Text.Trim().Length > 0)
            {
                section = new SectionDefinition(s.Text.Trim(), Pack, Path(file), item.Line);
            }
            else if (item is YamlMapping map && map["name"] is YamlScalar { IsNull: false } n && n.Text.Trim().Length > 0)
            {
                var required = map["required"];
                var description = map["description"];
                if (required is null or YamlScalar { IsNull: true } || required is YamlScalar r && r.AsBoolean() is not null)
                {
                    if (description is null or YamlScalar)
                    {
                        section = new SectionDefinition(n.Text.Trim(), Pack, Path(file), item.Line)
                        {
                            IsRequired = (required as YamlScalar)?.AsBoolean() == true,
                            Description = description is YamlScalar { IsNull: false } d ? d.Text : null,
                        };
                    }
                }

                foreach (var entry in map.Entries.Where(e => e.KeyText is not ("name" or "required" or "description")))
                {
                    Warn(file, "F-TYPE-9", entry.Key.Line, $"A section item has name, required, and description, not '{entry.KeyText}'. The key is kept and ignored.");
                }
            }

            if (section is null)
            {
                Warn(file, "F-TYPE-9", item.Line,
                    "A section item is a section name, or a mapping with a name and optionally required (true or false) and description (a string). This one is ignored.");
                continue;
            }

            if (result.FirstOrDefault(r => r.NormalizedName == section.NormalizedName) is { } earlier)
            {
                Warn(file, "F-TYPE-9", item.Line, $"The section '{section.Name}' is already listed on line {earlier.Line}, so this one is ignored.");
                continue;
            }

            result.Add(section);
        }

        return result;
    }

    /// <summary>F-TPL-1: a <c>template</c> value names a <c>.md</c> file in the pack's <c>templates/</c>.</summary>
    private string? CheckTemplate(string file, YamlNode? node)
    {
        if (node is null or YamlScalar { IsNull: true })
        {
            return null;
        }

        string? problem = null;
        if (node is not YamlScalar scalar)
        {
            problem = "template must be the file name of a template, such as 'template: ship.md'.";
        }
        else if (scalar.Text.Contains('/', StringComparison.Ordinal) || scalar.Text.Contains('\\', StringComparison.Ordinal)
            || !scalar.Text.EndsWith(".md", StringComparison.Ordinal))
        {
            problem = $"template '{scalar.Text}' must be a .md file name with no folder: templates are directly in the pack's templates/ folder.";
        }
        else if (!_content.Templates.ContainsKey(scalar.Text))
        {
            problem = $"template '{scalar.Text}' isn't in this pack's templates/ folder.";
        }

        if (problem is not null)
        {
            Warn(file, "F-TPL-1", node.Line, problem + " The file is read as if it had no template.");
            return null;
        }

        return ((YamlScalar)node).Text;
    }

    /// <summary>The <c>template</c> value of a type or extension file as written, for F-TPL-2, or null.</summary>
    private static string? TemplateValue(YamlFile file) =>
        !file.HasErrors && file.Root?["template"] is YamlScalar { IsNull: false } t
            ? t.Text
            : null;

    /// <summary>F-TPL-3: a template is checked like an entity file, with its own differences.</summary>
    private void ReadTemplate(string fileName)
    {
        var name = $"templates/{fileName}";
        if (BuiltInOrRead<MarkdownFile>(name, listed: true) is not { } file)
        {
            return;
        }

        var errors = 0;
        foreach (var d in file.Diagnostics)
        {
            Add(name, d);
            errors += d.Severity == DiagnosticSeverity.Error ? 1 : 0;
        }

        if (file.Frontmatter is { } fm)
        {
            void Error(string rule, int line, string message)
            {
                errors++;
                Add(name, new Diagnostic(rule, DiagnosticSeverity.Error, line, message));
            }

            if (fm["id"] is { } id && id is not YamlScalar { IsNull: true })
            {
                Error("F-TPL-3", id.Line, "A template can't have an id: every entity made from it would get the same one.");
            }

            if (fm["status"] is { } status && status is not YamlScalar { IsNull: true }
                && (status is not YamlScalar s || FactStatuses.Parse(s.Text) is null))
            {
                Error("F-STAT-1", status.Line, "A template's status must be idea, undecided, candidate, draft, canon, or retconned.");
            }

            switch (fm["aliases"])
            {
                case null or YamlScalar { IsNull: true }:
                    break;
                case YamlSequence list when list.Items.All(i => i is YamlScalar { IsNull: false } a && a.Text.Trim().Length > 0):
                    break;
                case var node:
                    Error("F-ENT-4", node.Line, "aliases must be a list of non-empty strings.");
                    break;
            }

            EntityCatalog.CheckFields(fm["fields"], Error);
            foreach (var d in PositionScanner.Scan(file).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                errors++;
                Add(name, d);
            }
        }
        else if (file.HasFrontmatter)
        {
            // Frontmatter that isn't a mapping: the parse error is already reported.
            errors = Math.Max(errors, 1);
        }

        _content.Templates[fileName] = new TemplateFile(fileName, Path(name), file, errors > 0);
    }

    /// <summary>Non-hidden files directly in a folder of the pack (F-LAY-2).</summary>
    private IEnumerable<string> Files(string folder) =>
        _source.GetFiles(Source(folder)).Where(n => !n.StartsWith('.')).Order(StringComparer.Ordinal);

    /// <summary>The <c>.yaml</c> files in <c>types/</c> or <c>extensions/</c>, warning about <c>.yml</c> ones (F-PACK-3).</summary>
    private List<string> YamlFiles(string folder)
    {
        var result = new List<string>();
        foreach (var name in Files(folder))
        {
            if (name.EndsWith(".yaml", StringComparison.Ordinal))
            {
                result.Add(name);
            }
            else if (name.EndsWith(".yml", StringComparison.Ordinal))
            {
                Warn($"{folder}/{name}", "F-PACK-3", null, $"{name} isn't read: did you mean {name[..^4]}.yaml?");
            }
        }

        return result;
    }

    /// <summary>Parses a pack's YAML file and adds its diagnostics, or returns null if it's gone or can't be read (#112).</summary>
    private YamlFile? Yaml(string name, bool listed = false)
    {
        var file = BuiltInOrRead<YamlFile>(name, listed);
        foreach (var d in file?.Diagnostics ?? [])
        {
            Add(name, d);
        }

        return file;
    }

    /// <summary>
    /// Parses a pack file, once per process for a built-in pack. Null for a
    /// file of the den's own that was listed but is gone or can't be read
    /// now, which is read as if it weren't there (#112). <paramref name="listed"/>
    /// says the name came from the folder's listing, so it isn't looked up again
    /// (<see cref="IDenSource.TryReadListedFile"/>).
    /// </summary>
    private T? BuiltInOrRead<T>(string name, bool listed = false)
        where T : DenFile
    {
        var path = Source(name);
        if (_source is BuiltInPacks builtIn)
        {
            return (T)builtIn.Parsed.GetOrAdd(path, p => Parse<T>(builtIn.ReadFile(p)));
        }

        var bytes = listed ? _source.TryReadListedFile(path) : _source.TryReadFile(path);
        return bytes is null ? null : Parse<T>(bytes);
    }

    private static T Parse<T>(byte[] bytes)
        where T : DenFile =>
        typeof(T) == typeof(YamlFile) ? (T)(DenFile)YamlFile.Parse(bytes) : (T)(DenFile)MarkdownFile.Parse(bytes);

    private string Source(string name) => name.Length == 0 ? _folder : $"{_folder}/{name}";

    /// <summary>A file's path as diagnostics name it.</summary>
    private string Path(string name) => $"{Pack.Folder}/{name}";

    private void Add(string name, Diagnostic diagnostic) => _content.Diagnostics.Add(new DenDiagnostic(Path(name), diagnostic));

    private void Error(string name, string rule, int? line, string message) =>
        Add(name, new Diagnostic(rule, DiagnosticSeverity.Error, line, message));

    private void Warn(string name, string rule, int? line, string message) =>
        Add(name, new Diagnostic(rule, DiagnosticSeverity.Warning, line, message));

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldName();
}
