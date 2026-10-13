using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Types;

/// <summary>
/// A chapter field's value that is set: in a chapter item's <c>fields</c>
/// or in a container's <c>chapter_defaults</c> (types.md F-CHAP-3,
/// F-CHAP-5), after the items that don't match the field's definition are
/// ignored.
/// </summary>
public sealed class ChapterFieldValue
{
    internal ChapterFieldValue(string name, FieldDefinition? definition, YamlNode value, IReadOnlyList<YamlNode> items, Container? defaultFrom)
    {
        Name = name;
        Definition = definition;
        Value = value;
        Items = items;
        DefaultFrom = defaultFrom;
    }

    /// <summary>The field name as written.</summary>
    public string Name { get; }

    /// <summary>The chapter field's definition (F-CHAP-2), or null for a name that isn't a chapter field (a warning, kept as written).</summary>
    public FieldDefinition? Definition { get; }

    /// <summary>The value as written.</summary>
    public YamlNode Value { get; }

    /// <summary>
    /// The items that hold: for a list field, the list items that match
    /// (a value that isn't a list is one item), and none for an explicit
    /// empty list, which means "none here"; otherwise the value itself.
    /// </summary>
    public IReadOnlyList<YamlNode> Items { get; }

    /// <summary>The container whose <c>chapter_defaults</c> gives the value, or null if it's the chapter's own.</summary>
    public Container? DefaultFrom { get; }

    /// <summary>True for a list field set to an empty list: "none here" (F-CHAP-5).</summary>
    public bool IsNone => Items.Count == 0;

    /// <summary>True if <paramref name="item"/> is one of <see cref="Items"/>, by reference.</summary>
    internal bool Holds(YamlNode item) => Items.Any(i => ReferenceEquals(i, item));

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Items.Count} item(s){(DefaultFrom is { } c ? $", from {c}" : "")})";
}
