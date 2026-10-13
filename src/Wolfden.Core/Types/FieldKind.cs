namespace Wolfden.Core.Types;

/// <summary>The field types of types.md §5.2.</summary>
public enum FieldKind
{
    /// <summary>One line of text (F-FT-4).</summary>
    String,

    /// <summary>Text that can run over several lines (F-FT-5).</summary>
    Text,

    /// <summary>A YAML integer or float (F-FT-6).</summary>
    Number,

    /// <summary><c>true</c> or <c>false</c> (F-FT-7).</summary>
    Bool,

    /// <summary>An in-world date: free text, a sortable key, or both (F-FT-11).</summary>
    Date,

    /// <summary>One of a fixed list of keys (F-FT-8).</summary>
    Enum,

    /// <summary>One link to an entity (F-FT-9).</summary>
    Ref,

    /// <summary>A story position (F-FT-10).</summary>
    Position,
}

/// <summary>Reading and writing <see cref="FieldKind"/> as pack files spell it.</summary>
public static class FieldKinds
{
    /// <summary>The field type named <paramref name="text"/>, exactly as types.md §5.2 spells it, or null.</summary>
    public static FieldKind? Parse(string text) => text switch
    {
        "string" => FieldKind.String,
        "text" => FieldKind.Text,
        "number" => FieldKind.Number,
        "bool" => FieldKind.Bool,
        "date" => FieldKind.Date,
        "enum" => FieldKind.Enum,
        "ref" => FieldKind.Ref,
        "position" => FieldKind.Position,
        _ => null,
    };

    /// <summary>The field type as pack files write it, such as <c>ref</c>.</summary>
    public static string ToKey(this FieldKind kind) => kind.ToString().ToLowerInvariant();
}
