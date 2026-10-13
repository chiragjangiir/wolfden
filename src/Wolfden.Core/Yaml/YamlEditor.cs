using System.Text;
using Wolfden.Core.Text;

namespace Wolfden.Core.Yaml;

/// <summary>A replacement of <see cref="Length"/> characters at <see cref="Start"/>.</summary>
internal readonly record struct TextEdit(int Start, int Length, string NewText)
{
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var sb = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Works out the smallest text edits that set a scalar in a YAML region.
/// It never re-serializes parsed YAML: it replaces the characters of one
/// value, or inserts one new entry after the existing keys (format.md
/// F-RT-2, F-RT-4).
/// </summary>
internal static class YamlEditor
{
    private const int DefaultIndent = 2;

    /// <summary>
    /// Edits that set <paramref name="path"/> (a key, then keys of nested
    /// block mappings) to <paramref name="value"/>, creating missing keys.
    /// </summary>
    /// <exception cref="InvalidOperationException">A key on the path holds something other than a mapping.</exception>
    /// <exception cref="NotSupportedException">The edit needs a layout the editor doesn't handle, such as a flow mapping.</exception>
    public static List<TextEdit> SetScalar(SourceText source, YamlBlock block, IReadOnlyList<string> path, YamlScalarValue value)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("The path is empty.", nameof(path));
        }

        foreach (var key in path)
        {
            if (!YamlScalarValue.IsSafeKey(key))
            {
                throw new ArgumentException($"'{key}' can't be written as a plain YAML key.", nameof(path));
            }
        }

        var nl = source.Newline;
        var unit = IndentUnit(source, block);
        if (block.Root is null)
        {
            // No document yet, maybe some comment lines: the new entry goes
            // after them, at the end of the region.
            var text = Chain(path, 0, 0, unit, value, nl, null);
            var at = block.RegionEnd;
            return at > block.RegionStart && source.Text[at - 1] is not ('\n' or '\r')
                ? [new TextEdit(at, 0, nl + text)]
                : [new TextEdit(at, 0, text + nl)];
        }

        if (block.Root is not YamlMapping mapping)
        {
            throw new InvalidOperationException("The top level isn't a mapping.");
        }

        for (var i = 0; ; i++)
        {
            if (mapping.IsFlow)
            {
                throw new NotSupportedException($"Can't add to the flow mapping at line {mapping.Line}.");
            }

            var entry = mapping.Find(path[i]);
            if (entry is null)
            {
                return [InsertEntry(source, mapping, path, i, unit, value, nl)];
            }

            if (i == path.Count - 1)
            {
                return ReplaceValue(source, entry, value);
            }

            switch (entry.Value)
            {
                case YamlMapping { IsFlow: false } child:
                    mapping = child;
                    continue;
                case YamlMapping { IsFlow: true, Entries.Count: 0 }:
                case YamlScalar { IsNull: true }:
                    return FillEmptyParent(source, entry, path, i + 1, unit, value, nl);
                case YamlMapping:
                    throw new NotSupportedException($"Can't add to the flow mapping '{path[i]}' at line {entry.Value.Line}.");
                default:
                    throw new InvalidOperationException($"'{path[i]}' at line {entry.Key.Line} isn't a mapping.");
            }
        }
    }

    /// <summary>The text of new entries for <c>path[from..]</c>; the first line
    /// carries no indent (the caller places it), later lines do.</summary>
    private static string Chain(IReadOnlyList<string> path, int from, int indent, int unit, YamlScalarValue value, string nl, YamlScalarStyle? existing)
    {
        var sb = new StringBuilder();
        for (var i = from; i < path.Count; i++)
        {
            if (i > from)
            {
                sb.Append(nl).Append(' ', indent + (unit * (i - from)));
            }

            sb.Append(path[i]).Append(':');
            if (i == path.Count - 1)
            {
                var token = value.Render(existing);
                if (token.Length > 0)
                {
                    sb.Append(' ').Append(token);
                }
            }
        }

        return sb.ToString();
    }

    private static TextEdit InsertEntry(SourceText source, YamlMapping mapping, IReadOnlyList<string> path, int from, int unit, YamlScalarValue value, string nl)
    {
        var first = mapping.Entries[0].Key;
        var indent = Column(source, first.Start);
        var lastLine = source.Lines[LineIndexOfEnd(source, mapping)];
        var text = Chain(path, from, indent, unit, value, nl, null);
        var pad = new string(' ', indent);
        return lastLine.TerminatorLength > 0
            ? new TextEdit(lastLine.EndIncludingTerminator, 0, pad + text + nl)
            : new TextEdit(lastLine.End, 0, nl + pad + text);
    }

    private static List<TextEdit> ReplaceValue(SourceText source, YamlEntry entry, YamlScalarValue value)
    {
        var current = entry.Value;
        if (value.Matches(current))
        {
            // Already says that: a write that changes nothing (F-RT-3).
            return [];
        }

        var colon = ColonAfter(source, entry.Key);
        var existing = (current as YamlScalar)?.Style;
        var token = value.Render(existing);
        var spaced = token.Length > 0 ? " " + token : "";

        if (current is YamlScalar { Style: YamlScalarStyle.Plain or YamlScalarStyle.SingleQuoted or YamlScalarStyle.DoubleQuoted } scalar)
        {
            if (scalar.Start == scalar.ContentEnd)
            {
                // `key:` with nothing after it, maybe with a comment.
                return token.Length == 0 ? [] : [new TextEdit(colon + 1, 0, spaced)];
            }

            // Keep the spacing before the value and any comment after it.
            return token.Length > 0
                ? [new TextEdit(scalar.Start, scalar.ContentEnd - scalar.Start, token)]
                : [new TextEdit(colon + 1, scalar.ContentEnd - colon - 1, "")];
        }

        var end = current.ContentEnd;
        if (current is YamlMapping { IsFlow: false } or YamlSequence { IsFlow: false })
        {
            // A block collection ends with its last line, including any
            // comment on it; comment lines after it stay.
            end = source.Lines[LineIndexOfEnd(source, current)].End;
        }

        return [new TextEdit(colon + 1, end - colon - 1, spaced)];
    }

    private static List<TextEdit> FillEmptyParent(SourceText source, YamlEntry entry, IReadOnlyList<string> path, int from, int unit, YamlScalarValue value, string nl)
    {
        var colon = ColonAfter(source, entry.Key);
        var edits = new List<TextEdit>();
        var current = entry.Value;
        if (current.ContentEnd > current.Start)
        {
            // Drop the `~` or `{}`, keeping any comment after it.
            edits.Add(new TextEdit(colon + 1, current.ContentEnd - colon - 1, ""));
        }

        var lineIndex = source.LineIndexOf(Math.Max(colon, current.ContentEnd - 1));
        var line = source.Lines[lineIndex];
        var indent = Column(source, entry.Key.Start) + unit;
        var text = Chain(path, from, indent, unit, value, nl, null);
        edits.Add(new TextEdit(line.End, 0, nl + new string(' ', indent) + text));
        return edits;
    }

    /// <summary>The offset of the colon that follows a block mapping key.</summary>
    private static int ColonAfter(SourceText source, YamlNode key)
    {
        var text = source.Text;
        var i = key.ContentEnd;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        if (i >= text.Length || text[i] != ':')
        {
            throw new NotSupportedException($"The key at line {key.Line} isn't written as `key: value`.");
        }

        return i;
    }

    private static int LineIndexOfEnd(SourceText source, YamlNode node) => source.LineIndexOf(node.LastLineOffset);

    private static int Column(SourceText source, int offset) => offset - source.Lines[source.LineIndexOf(offset)].Start;

    /// <summary>
    /// The indent step the file already uses for nested mappings (the first
    /// one found), or two spaces.
    /// </summary>
    private static int IndentUnit(SourceText source, YamlBlock block)
    {
        return block.Root is YamlMapping root ? Find(root) ?? DefaultIndent : DefaultIndent;

        int? Find(YamlMapping mapping)
        {
            if (mapping.IsFlow)
            {
                return null;
            }

            foreach (var entry in mapping.Entries)
            {
                if (entry.Value is YamlMapping { IsFlow: false, Entries.Count: > 0 } child)
                {
                    var step = Column(source, child.Entries[0].Key.Start) - Column(source, entry.Key.Start);
                    if (step > 0)
                    {
                        return step;
                    }
                }
            }

            return null;
        }
    }
}
