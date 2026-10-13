namespace Wolfden.Core.Yaml;

/// <summary>How a scalar was written.</summary>
public enum YamlScalarStyle
{
    /// <summary>Unquoted.</summary>
    Plain,

    /// <summary><c>'single quoted'</c>.</summary>
    SingleQuoted,

    /// <summary><c>"double quoted"</c>.</summary>
    DoubleQuoted,

    /// <summary>A <c>|</c> block scalar.</summary>
    Literal,

    /// <summary>A <c>&gt;</c> block scalar.</summary>
    Folded,
}

/// <summary>
/// A parsed YAML value, for reading. Writers never serialize these back:
/// edits go through the owning document, which changes only the bytes of
/// the value it edits (format.md F-RT-2).
/// </summary>
public abstract class YamlNode
{
    private protected YamlNode(int line, int start, int contentEnd)
    {
        Line = line;
        Start = start;
        ContentEnd = contentEnd;
    }

    /// <summary>The 1-based line in the file where the node starts.</summary>
    public int Line { get; }

    /// <summary>Offset of the node's first character in the document text.</summary>
    internal int Start { get; }

    /// <summary>Offset just after the node's last character, not counting
    /// trailing blank lines or comments.</summary>
    internal int ContentEnd { get; }
}

/// <summary>A YAML scalar.</summary>
public sealed class YamlScalar : YamlNode
{
    internal YamlScalar(string text, YamlScalarStyle style, int line, int start, int contentEnd)
        : base(line, start, contentEnd)
    {
        Text = text;
        Style = style;
    }

    /// <summary>
    /// The scalar's text as written, after YAML unquoting and folding but
    /// before type resolution (format.md F-YAML-5): <c>014</c> stays
    /// <c>014</c>, and <c>1984</c> is the text "1984".
    /// </summary>
    public string Text { get; }

    /// <summary>How the scalar was written.</summary>
    public YamlScalarStyle Style { get; }

    /// <summary>
    /// True for a YAML null under the core schema: a plain <c>~</c>,
    /// <c>null</c>, <c>Null</c>, <c>NULL</c>, or nothing at all
    /// (format.md F-YAML-5, F-FLD-4).
    /// </summary>
    public bool IsNull => Style == YamlScalarStyle.Plain && Text is "" or "~" or "null" or "Null" or "NULL";

    /// <summary>The scalar as a core-schema boolean, if it's a plain <c>true</c> or <c>false</c>.</summary>
    public bool? AsBoolean() => Style != YamlScalarStyle.Plain ? null : Text switch
    {
        "true" or "True" or "TRUE" => true,
        "false" or "False" or "FALSE" => false,
        _ => null,
    };

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>A YAML sequence (list).</summary>
public sealed class YamlSequence : YamlNode
{
    internal YamlSequence(IReadOnlyList<YamlNode> items, bool isFlow, int line, int start, int contentEnd)
        : base(line, start, contentEnd)
    {
        Items = items;
        IsFlow = isFlow;
    }

    /// <summary>The items, in file order.</summary>
    public IReadOnlyList<YamlNode> Items { get; }

    /// <summary>True for <c>[a, b]</c>, false for a block list.</summary>
    public bool IsFlow { get; }
}

/// <summary>One key and value in a <see cref="YamlMapping"/>.</summary>
/// <param name="Key">The key. Usually a scalar.</param>
/// <param name="Value">The value. A missing value is a null scalar.</param>
public sealed record YamlEntry(YamlNode Key, YamlNode Value)
{
    /// <summary>The key's text, or null if the key isn't a scalar.</summary>
    public string? KeyText => (Key as YamlScalar)?.Text;
}

/// <summary>A YAML mapping, with its keys in file order.</summary>
public sealed class YamlMapping : YamlNode
{
    internal YamlMapping(IReadOnlyList<YamlEntry> entries, bool isFlow, int line, int start, int contentEnd)
        : base(line, start, contentEnd)
    {
        Entries = entries;
        IsFlow = isFlow;
    }

    /// <summary>The entries, in file order (format.md F-RT-1 keeps key order).</summary>
    public IReadOnlyList<YamlEntry> Entries { get; }

    /// <summary>True for <c>{ a: 1 }</c>, false for a block mapping.</summary>
    public bool IsFlow { get; }

    /// <summary>The keys' texts, in file order.</summary>
    public IEnumerable<string?> Keys => Entries.Select(e => e.KeyText);

    /// <summary>The value for <paramref name="key"/>, or null if the key is missing.
    /// With a repeated key (an F-YAML-3 error), the first one wins.</summary>
    public YamlNode? this[string key] => Find(key)?.Value;

    /// <summary>The entry for <paramref name="key"/>, or null.</summary>
    public YamlEntry? Find(string key)
    {
        foreach (var entry in Entries)
        {
            if (entry.KeyText == key)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>True if the key is present, even with a null value.</summary>
    public bool ContainsKey(string key) => Find(key) is not null;
}
