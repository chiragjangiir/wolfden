using Wolfden.Core.Documents;
using Wolfden.Core.Links;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Types;

/// <summary>An enabled pack (types.md §2): built in, or a folder in the den's <c>packs/</c>.</summary>
public sealed class PackDefinition
{
    internal PackDefinition(string key, bool isBuiltIn, string folder)
    {
        Key = key;
        IsBuiltIn = isBuiltIn;
        Folder = folder;
    }

    /// <summary>The pack key: its folder name (F-PACK-3).</summary>
    public string Key { get; }

    /// <summary>True for a pack that ships with Wolfden (F-PACK-4), false for a den-local one (F-PACK-5).</summary>
    public bool IsBuiltIn { get; }

    /// <summary>
    /// The folder that diagnostics name: <c>packs/&lt;key&gt;</c> relative to
    /// the den root for a den-local pack, or the built-in prefix
    /// followed by <c>packs/&lt;key&gt;</c> for a built-in one.
    /// </summary>
    public string Folder { get; }

    /// <summary>The display name from <c>pack.yaml</c> (F-PACK-6).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The description from <c>pack.yaml</c>, or null.</summary>
    public string? Description { get; internal set; }

    /// <summary>The pack's own release version from <c>pack.yaml</c>, or null.</summary>
    public string? Version { get; internal set; }

    /// <summary>True for a den-local pack whose key is a built-in pack's key (F-PACK-10).</summary>
    public bool Shadows { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => IsBuiltIn ? $"built-in pack '{Key}'" : $"pack '{Key}'";
}

/// <summary>A field definition (types.md §5.1), for an entity type or a chapter field (§8).</summary>
public sealed class FieldDefinition
{
    private IReadOnlyList<string> _checkedTo = [];

    internal FieldDefinition(string name, FieldKind kind, PackDefinition pack, string path, int line)
    {
        Name = name;
        Kind = kind;
        Pack = pack;
        Path = path;
        Line = line;
    }

    /// <summary>The field name (F-FLD-1).</summary>
    public string Name { get; }

    /// <summary>The field type (F-FT-2).</summary>
    public FieldKind Kind { get; }

    /// <summary>True if the value is a list of <see cref="Kind"/> (F-FT-12).</summary>
    public bool IsList { get; internal set; }

    /// <summary>True if the field may have temporal entries (F-FT-13). Always false for a chapter field.</summary>
    public bool IsTemporal { get; internal set; }

    /// <summary>True if a missing value is a warning (F-FT-14). Always false for a chapter field.</summary>
    public bool IsRequired { get; internal set; }

    /// <summary>
    /// The value views show when the field has none (F-FT-15), as parsed
    /// from the pack file, or null. It's checked against the field's type,
    /// and a bad one is dropped with a warning.
    /// </summary>
    public YamlNode? Default { get; internal set; }

    /// <summary>For an <c>enum</c>, the allowed keys, lowercased, in order (F-FT-8). Empty otherwise.</summary>
    public IReadOnlyList<string> Values { get; internal set; } = [];

    /// <summary>For a <c>ref</c>, the type keys of <c>to</c> as written, or null if it links to any type (F-FT-9).</summary>
    public IReadOnlyList<string>? To { get; internal set; }

    /// <summary>
    /// The type keys the F-LINK-11 check accepts: <see cref="To"/> without
    /// the names that aren't enabled types, which the check skips (F-FT-9).
    /// Empty means any type.
    /// </summary>
    public IReadOnlyList<string> CheckedTo
    {
        get => _checkedTo;
        internal set => _checkedTo = value;
    }

    /// <summary>The description, shown in the app and given to AI agents, or null.</summary>
    public string? Description { get; internal set; }

    /// <summary>The pack that defines it.</summary>
    public PackDefinition Pack { get; }

    /// <summary>The pack file that defines it, as diagnostics name it.</summary>
    public string Path { get; }

    /// <summary>The 1-based line of the field's name in that file.</summary>
    public int Line { get; }

    /// <summary>
    /// The defined key that <paramref name="text"/> matches, after Unicode
    /// case folding (F-FT-8), so <c>Past</c> and <c>PAST</c> are both
    /// <c>past</c>. Null if it matches none, or the field isn't an enum.
    /// </summary>
    public string? MatchEnum(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = LinkText.CaseFold(text);
        return Values.FirstOrDefault(v => string.Equals(v, folded, StringComparison.Ordinal));
    }

    /// <summary>
    /// True if a <c>ref</c> may link to an entity of type <paramref name="typeKey"/> (F-LINK-11):
    /// it has no checked <c>to</c>, or <c>to</c> names that type.
    /// </summary>
    public bool Accepts(string typeKey)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        return CheckedTo.Count == 0 || CheckedTo.Contains(typeKey, StringComparer.Ordinal);
    }

    /// <summary>Describes the field type for messages, such as "a list of ref to character or faction".</summary>
    public string Describe()
    {
        var kind = Kind switch
        {
            FieldKind.Ref when To is { Count: > 0 } to => $"ref to {string.Join(" or ", to)}",
            FieldKind.Enum => $"enum ({string.Join(", ", Values)})",
            _ => Kind.ToKey(),
        };
        return IsList ? $"a list of {kind}" : kind;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Describe()}";
}

/// <summary>A standard section of a type (types.md §6).</summary>
public sealed class SectionDefinition
{
    internal SectionDefinition(string name, PackDefinition pack, string path, int line)
    {
        Name = name;
        NormalizedName = LinkText.Normalize(name);
        Pack = pack;
        Path = path;
        Line = line;
    }

    /// <summary>The section name as written.</summary>
    public string Name { get; }

    /// <summary>True if a missing section is a warning (F-TYPE-11).</summary>
    public bool IsRequired { get; internal set; }

    /// <summary>What the section is for, or null.</summary>
    public string? Description { get; internal set; }

    /// <summary>The pack that defines it.</summary>
    public PackDefinition Pack { get; }

    /// <summary>The pack file that defines it, as diagnostics name it.</summary>
    public string Path { get; }

    /// <summary>The 1-based line of the item in that file.</summary>
    public int Line { get; }

    /// <summary>The name after F-LINK-4 normalization, which section names match under (F-SEC-3).</summary>
    internal string NormalizedName { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A type's template (types.md §7): a file in a pack's <c>templates/</c>, or the generated one (F-TPL-2).</summary>
public sealed class TemplateDefinition
{
    internal TemplateDefinition(PackDefinition? pack, string? fileName, string path, MarkdownFile file)
    {
        Pack = pack;
        FileName = fileName;
        Path = path;
        File = file;
    }

    /// <summary>The pack whose <c>templates/</c> holds it, or null for a generated template.</summary>
    public PackDefinition? Pack { get; }

    /// <summary>The template's file name, such as <c>thread.md</c>, or null for a generated template.</summary>
    public string? FileName { get; }

    /// <summary>True for the generated template (F-TPL-2).</summary>
    public bool IsGenerated => FileName is null;

    /// <summary>The template file as diagnostics name it, or the type file's path for a generated template.</summary>
    public string Path { get; }

    /// <summary>The parsed template. Its frontmatter is optional (F-TPL-3).</summary>
    public MarkdownFile File { get; }

    /// <summary>The template's text, before <c>{{name}}</c> is replaced (F-TPL-4).</summary>
    public string Text => File.Text;

    /// <inheritdoc />
    public override string ToString() => FileName ?? "(generated)";
}

/// <summary>
/// An enabled type's <b>combined definition</b> (types.md F-TYPE-8): its own
/// definition with the enabled extensions applied, in pack order.
/// </summary>
public sealed class TypeDefinition
{
    /// <summary>The fields every type has, after its own in the field order (types.md §5.5).</summary>
    public static readonly IReadOnlyList<string> UniversalFields = ["row", "placeholder"];

    private readonly Dictionary<string, FieldDefinition> _fieldsByName;
    private readonly Dictionary<string, SectionDefinition> _sectionsByName;

    internal TypeDefinition(string key, PackDefinition pack, string path, string name, string plural, string? description,
        IReadOnlyList<FieldDefinition> fields, IReadOnlyList<SectionDefinition> sections, TemplateDefinition template)
    {
        Key = key;
        Pack = pack;
        Path = path;
        Name = name;
        Plural = plural;
        Description = description;
        Fields = fields;
        Sections = sections;
        Template = template;
        _fieldsByName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        _sectionsByName = new Dictionary<string, SectionDefinition>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            _sectionsByName.TryAdd(section.NormalizedName, section);
        }

        PositionFields = fields.Where(f => f.Kind == FieldKind.Position).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The type key: what entities write in <c>type:</c> (F-TYPE-1).</summary>
    public string Key { get; }

    /// <summary>The pack whose <c>types/</c> defines it (F-TYPE-4).</summary>
    public PackDefinition Pack { get; }

    /// <summary>True if a built-in pack defines it.</summary>
    public bool IsBuiltIn => Pack.IsBuiltIn;

    /// <summary>The type file, as diagnostics name it.</summary>
    public string Path { get; }

    /// <summary>The display name, such as "Character" (F-TYPE-3).</summary>
    public string Name { get; }

    /// <summary>The folder name under <c>entities/</c>, such as <c>characters</c> (F-TYPE-3, F-LAY-6).</summary>
    public string Plural { get; }

    /// <summary>The description, or null.</summary>
    public string? Description { get; }

    /// <summary>The fields in field order (F-FT-3), without the universal <c>row</c> and <c>placeholder</c>.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>The standard sections in section order (F-TYPE-10, F-TYPE-12).</summary>
    public IReadOnlyList<SectionDefinition> Sections { get; }

    /// <summary>The template new entities of this type start from (§7).</summary>
    public TemplateDefinition Template { get; }

    /// <summary>The names of the fields whose type is <c>position</c> (F-FT-10), such as a thread's <c>planted</c> and <c>paid_off</c>.</summary>
    public IReadOnlySet<string> PositionFields { get; }

    /// <summary>
    /// True if this is the built-in core pack's <c>thread</c>, extended or
    /// not, whose <c>## Payoff</c> takes its <c>paid_off</c> as its
    /// <c>from</c> (F-CORE-4). A den-local type that replaces it isn't.
    /// </summary>
    public bool IsCoreThread => Key == "thread" && Pack.IsBuiltIn && Pack.Key == "core";

    /// <summary>
    /// True if this is the built-in core pack's <c>event</c>, extended or
    /// not, whose <c>ends</c> is checked against its <c>when</c> (F-CORE-3).
    /// </summary>
    public bool IsCoreEvent => Key == "event" && Pack.IsBuiltIn && Pack.Key == "core";

    /// <summary>The field named <paramref name="name"/>, or null if the type doesn't define it.</summary>
    public FieldDefinition? FindField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _fieldsByName.GetValueOrDefault(name);
    }

    /// <summary>The standard section whose name matches <paramref name="sectionName"/> (F-SEC-3), or null.</summary>
    public SectionDefinition? FindSection(string sectionName)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        return _sectionsByName.GetValueOrDefault(LinkText.Normalize(sectionName));
    }

    /// <summary>True if a section with this name is one of the type's standard sections (F-TYPE-10).</summary>
    public bool IsStandardSection(string sectionName) => FindSection(sectionName) is not null;

    /// <summary>True for a field every type has: <c>row</c> and <c>placeholder</c> (§5.5).</summary>
    public static bool IsUniversalField(string name) => name is "row" or "placeholder";

    /// <inheritdoc />
    public override string ToString() => $"{Key} ({Pack})";
}
