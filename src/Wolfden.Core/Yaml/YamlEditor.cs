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
/// Works out the smallest text edits that change a YAML region: set a
/// scalar, add or remove a list item, or remove a key. It never
/// re-serializes parsed YAML: it replaces the characters of one value,
/// inserts one new entry or item next to the existing ones, or deletes the
/// characters of one (format.md F-RT-2, F-RT-4).
/// </summary>
/// <remarks>
/// A path is a list of segments, read like a JSON Pointer: a segment names
/// a key in a mapping, and in a list it's an item's number from 0
/// (<c>["fields", "rank", "1", "from"]</c>). Which one it is depends on
/// what the file holds at that point, so a chapter key such as <c>14</c>
/// still names a key in a mapping.
/// </remarks>
internal static partial class YamlEditor
{
    private const int DefaultIndent = 2;

    /// <summary>
    /// Edits that set <paramref name="path"/> to <paramref name="value"/>,
    /// creating missing keys. The path runs through block and flow mappings,
    /// and through lists by item number.
    /// </summary>
    /// <exception cref="InvalidOperationException">Something on the path isn't a mapping or list, or a list has no such item.</exception>
    /// <exception cref="NotSupportedException">The edit needs a layout the editor doesn't handle in place.</exception>
    /// <param name="templateOrder">True for an entity or container file's frontmatter, where a new top-level key from the
    /// template order (types.md F-TPL-7) goes in its place in that order rather than after the existing keys.</param>
    public static List<TextEdit> SetScalar(SourceText source, YamlBlock block, IReadOnlyList<string> path, YamlScalarValue value, bool templateOrder = false) =>
        SetValue(source, block, path, new ScalarNew(value), templateOrder);

    /// <summary>
    /// Edits that set <paramref name="path"/> to <paramref name="value"/>, a
    /// scalar, list, or mapping, creating missing keys. A list or mapping is
    /// written in flow style on the key's line (<c>aliases: [a, b]</c>,
    /// <c>died: { value: x, from: "a1:3" }</c>), except a non-empty list of
    /// mappings under a block key, such as a temporal list, which gets a
    /// <c>- { ... }</c> line per item as format.md §7.3 writes them. The
    /// value at the path must be missing, null, or a scalar: a collection is
    /// replaced by setting it to null first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Something on the path isn't a mapping or list, or a list has no such item.</exception>
    /// <exception cref="NotSupportedException">The edit needs a layout the editor doesn't handle in place.</exception>
    public static List<TextEdit> SetAny(SourceText source, YamlBlock block, IReadOnlyList<string> path, YamlValue value, bool templateOrder = false) =>
        value is YamlScalarValue scalar
            ? SetScalar(source, block, path, scalar, templateOrder)
            : SetValue(source, block, path, new AnyNew(value, source.Newline), templateOrder);

    /// <summary>
    /// Edits that add <paramref name="item"/> to the list at
    /// <paramref name="path"/>, before the item numbered
    /// <paramref name="index"/>, or at the end. The new item takes the list's
    /// style: a flow list stays on its lines, and a block list gets a new
    /// <c>- item</c> line at the same indent. A missing or null value becomes
    /// a new list.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is past the end of the list.</exception>
    /// <exception cref="InvalidOperationException">The value at the path isn't a list.</exception>
    /// <exception cref="NotSupportedException">The list's layout can't take a new item without moving a comment.</exception>
    public static List<TextEdit> AddItem(SourceText source, YamlBlock block, IReadOnlyList<string> path, YamlValue item, int? index, bool templateOrder = false)
    {
        CheckPath(path);
        var node = Find(block.Root, path);
        if (node is YamlSequence sequence)
        {
            var count = sequence.Items.Count;
            var at = index ?? count;
            if (at < 0 || at > count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"The list has {count} items, so a new one goes at 0 to {count}.");
            }

            var like = at > 0 ? sequence.Items[at - 1] : count > 0 ? sequence.Items[0] : null;
            return sequence.IsFlow
                ? FlowInsert(source, sequence, item.RenderFlow(like, source), at)
                : BlockInsertItem(source, sequence, item, at);
        }

        if (node is not (null or YamlScalar { IsNull: true }))
        {
            throw new InvalidOperationException($"'{Join(path, path.Count)}' at line {node.Line} isn't a list.");
        }

        if (index is not (null or 0))
        {
            throw new ArgumentOutOfRangeException(nameof(index), "There's no list yet, so a new item goes at 0.");
        }

        if (node is not null && path.Count > 1 && Find(block.Root, path.Take(path.Count - 1).ToList()) is YamlSequence)
        {
            throw new NotSupportedException($"'{Join(path, path.Count)}' at line {node.Line} is an empty list item. Replace the item instead.");
        }

        return SetValue(source, block, path, new ListNew(item), templateOrder);
    }

    /// <summary>
    /// Edits that remove the key or list item at <paramref name="path"/>,
    /// with its lines: a comment on its last line, and comment lines right
    /// after it indented under it, go with it; comment lines above it stay.
    /// A block list or mapping that loses its last item is left as
    /// <c>key:</c> with no value (format.md F-RT-6), or for a block list
    /// with <paramref name="emptied"/> set to <see cref="EmptiedList.EmptyList"/>,
    /// as <c>key: []</c>; a flow one becomes <c>[]</c> or <c>{}</c>.
    /// </summary>
    /// <remarks>
    /// Nothing there means nothing to remove, so no edits: a missing key, an
    /// item number past the end of a list, or a null value on the way. A
    /// path that doesn't fit the file's shape is an error, the same as for
    /// setting a value: a segment that isn't an item number where the file
    /// holds a list, or a path that runs into a scalar.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The path doesn't fit the file's shape.</exception>
    /// <exception cref="NotSupportedException">The removal would delete or move a comment that belongs to something else,
    /// or the key is written in a form that can't be removed in place.</exception>
    public static List<TextEdit> Remove(SourceText source, YamlBlock block, IReadOnlyList<string> path, EmptiedList emptied = EmptiedList.Null)
    {
        CheckPath(path);
        YamlNode? node = block.Root;
        YamlEntry? holder = null;
        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var last = i == path.Count - 1;
            switch (node)
            {
                case null or YamlScalar { IsNull: true }:
                    return [];
                case YamlMapping mapping:
                {
                    var k = IndexOfKey(mapping, segment);
                    if (k < 0)
                    {
                        return [];
                    }

                    if (last)
                    {
                        return mapping.IsFlow ? FlowRemove(source, mapping, k) : BlockRemoveEntry(source, mapping, k);
                    }

                    holder = mapping.Entries[k];
                    node = holder.Value;
                    break;
                }

                case YamlSequence sequence:
                {
                    if (!ParseIndex(segment, out var k))
                    {
                        throw new InvalidOperationException(
                            $"'{Join(path, i)}' at line {sequence.Line} is a list, and '{segment}' isn't an item number.");
                    }

                    if (k >= sequence.Items.Count)
                    {
                        return [];
                    }

                    if (last)
                    {
                        return sequence.IsFlow
                            ? FlowRemove(source, sequence, k)
                            : BlockRemoveItem(source, sequence, k, emptied, holder);
                    }

                    holder = null;
                    node = sequence.Items[k];
                    break;
                }

                default:
                    throw new InvalidOperationException($"'{Join(path, i)}' at line {node.Line} isn't a mapping or a list.");
            }
        }

        return [];
    }

    /// <summary>Finds the node at <paramref name="path"/>: keys in mappings, item numbers in lists.</summary>
    internal static YamlNode? Find(YamlNode? root, IReadOnlyList<string> path)
    {
        var node = root;
        foreach (var segment in path)
        {
            node = node switch
            {
                YamlMapping mapping => mapping[segment],
                YamlSequence sequence when TryIndex(segment, sequence.Items.Count, out var i) => sequence.Items[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>True if <paramref name="segment"/> is an item number below
    /// <paramref name="count"/>, written in digits with no leading zero.</summary>
    internal static bool TryIndex(string segment, int count, out int index) => ParseIndex(segment, out index) && index < count;

    /// <summary>
    /// True if <paramref name="segment"/> is written as an item number: digits
    /// only, of any length, with no leading zero. A number too large for an
    /// <see cref="int"/> is <see cref="int.MaxValue"/>, past the end of any list.
    /// </summary>
    internal static bool ParseIndex(string segment, out int index)
    {
        index = -1;
        if (segment.Length == 0 || !segment.All(char.IsAsciiDigit) || (segment.Length > 1 && segment[0] == '0'))
        {
            return false;
        }

        index = segment.Length > 9 || !int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? int.MaxValue
            : parsed;
        return true;
    }

    private static void CheckPath(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new ArgumentException("The path is empty.", nameof(path));
        }
    }

    /// <summary>Keys <c>path[from..]</c> are about to be written: each must be a plain key.</summary>
    private static void CheckNewKeys(IReadOnlyList<string> path, int from)
    {
        for (var i = from; i < path.Count; i++)
        {
            if (!YamlScalarValue.IsSafeKey(path[i]))
            {
                throw new ArgumentException($"'{path[i]}' can't be written as a plain YAML key.", nameof(path));
            }
        }
    }

    private static string Join(IReadOnlyList<string> path, int count) => string.Join('.', path.Take(count));

    private static int IndexOfKey(YamlMapping mapping, string key)
    {
        for (var i = 0; i < mapping.Entries.Count; i++)
        {
            if (mapping.Entries[i].KeyText == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<TextEdit> SetValue(SourceText source, YamlBlock block, IReadOnlyList<string> path, NewValue value, bool templateOrder)
    {
        CheckPath(path);
        var nl = source.Newline;
        var unit = IndentUnit(source, block);
        if (block.Root is null)
        {
            // No document yet, maybe some comment lines: the new entry goes
            // after them, at the end of the region.
            CheckNewKeys(path, 0);
            var text = Chain(path, 0, 0, unit, value, nl);
            var at = block.RegionEnd;
            return at > block.RegionStart && source.Text[at - 1] is not ('\n' or '\r')
                ? [new TextEdit(at, 0, nl + text)]
                : [new TextEdit(at, 0, text + nl)];
        }

        if (block.Root is not YamlMapping)
        {
            throw new InvalidOperationException("The top level isn't a mapping.");
        }

        YamlNode node = block.Root;
        for (var i = 0; ; i++)
        {
            var last = i == path.Count - 1;
            if (node is YamlSequence sequence)
            {
                if (!TryIndex(path[i], sequence.Items.Count, out var k))
                {
                    throw new InvalidOperationException(
                        $"'{Join(path, i)}' at line {sequence.Line} is a list, and '{path[i]}' isn't the number of one of its {sequence.Items.Count} items.");
                }

                var item = sequence.Items[k];
                if (last)
                {
                    return ReplaceItem(source, sequence, k, value);
                }

                switch (item)
                {
                    case YamlMapping { Entries.Count: > 0 } or YamlSequence:
                        node = item;
                        continue;
                    case YamlMapping or YamlScalar { IsNull: true }:
                        throw new NotSupportedException($"Item {k} of '{Join(path, i)}' at line {item.Line} is empty, so it can't be filled in place. Replace the item instead.");
                    default:
                        throw new InvalidOperationException($"Item {k} of '{Join(path, i)}' at line {item.Line} isn't a mapping.");
                }
            }

            var mapping = (YamlMapping)node;
            var entry = mapping.Find(path[i]);
            if (entry is null)
            {
                CheckNewKeys(path, i);
                return mapping.IsFlow
                    ? FlowInsert(source, mapping, FlowChain(path, i, value), mapping.Entries.Count)
                    : [templateOrder && i == 0 && TemplateRank(path[0]) is { } rank ? InsertTopLevelInOrder(source, mapping, path, rank, unit, value, nl) : InsertEntry(source, mapping, path, i, unit, value, nl)];
            }

            if (last)
            {
                return ReplaceValue(source, entry, value, mapping.IsFlow, unit);
            }

            switch (entry.Value)
            {
                case YamlMapping { Entries.Count: > 0 } or YamlSequence:
                    node = entry.Value;
                    continue;
                case YamlMapping or YamlScalar { IsNull: true }:
                    CheckNewKeys(path, i + 1);
                    return mapping.IsFlow
                        ? [FillFlowEmpty(source, entry, path, i + 1, value)]
                        : FillEmptyParent(source, entry, path, i + 1, unit, value, nl);
                default:
                    throw new InvalidOperationException($"'{path[i]}' at line {entry.Key.Line} isn't a mapping.");
            }
        }
    }

    /// <summary>The text of new block entries for <c>path[from..]</c>; the first line
    /// carries no indent (the caller places it), later lines do.</summary>
    private static string Chain(IReadOnlyList<string> path, int from, int indent, int unit, NewValue value, string nl)
    {
        var sb = new StringBuilder();
        for (var i = from; i < path.Count; i++)
        {
            var keyIndent = indent + (unit * (i - from));
            if (i > from)
            {
                sb.Append(nl).Append(' ', keyIndent);
            }

            sb.Append(path[i]).Append(':');
            if (i == path.Count - 1)
            {
                var token = value.Inline(flow: false, existing: null);
                if (token is null)
                {
                    sb.Append(nl).Append(value.Lines(keyIndent, unit));
                }
                else if (token.Length > 0)
                {
                    sb.Append(' ').Append(token);
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>The text of a new flow entry for <c>path[from..]</c>: <c>a: { b: value }</c>.</summary>
    private static string FlowChain(IReadOnlyList<string> path, int from, NewValue value)
    {
        var sb = new StringBuilder();
        for (var i = from; i < path.Count; i++)
        {
            sb.Append(path[i]).Append(": ").Append(i < path.Count - 1 ? "{ " : value.Inline(flow: true, existing: null));
        }

        for (var i = from; i < path.Count - 1; i++)
        {
            sb.Append(" }");
        }

        return sb.ToString();
    }

    private static TextEdit InsertEntry(SourceText source, YamlMapping mapping, IReadOnlyList<string> path, int from, int unit, NewValue value, string nl)
    {
        var first = mapping.Entries[0].Key;
        var indent = Column(source, first.Start);
        var lastLine = source.Lines[LineIndexOfEnd(source, mapping)];
        var text = Chain(path, from, indent, unit, value, nl);
        var pad = new string(' ', indent);
        return lastLine.TerminatorLength > 0
            ? new TextEdit(lastLine.EndIncludingTerminator, 0, pad + text + nl)
            : new TextEdit(lastLine.End, 0, nl + pad + text);
    }

    /// <summary>The top-level keys of an entity file in template order (types.md F-TPL-7).</summary>
    private static readonly string[] TemplateOrder = ["id", "type", "name", "aliases", "status", "from", "fields"];

    private static int? TemplateRank(string key) => Array.IndexOf(TemplateOrder, key) is var i and >= 0 ? i : null;

    /// <summary>
    /// A new top-level key that has a place in the template order (types.md
    /// F-TPL-7, format.md F-RT-4): right after the nearest key before it in
    /// that order that the file has, with the comment lines indented under
    /// that key; or, if there's none, just before the first key from that
    /// order; or, if the file has no key from it, after the existing keys.
    /// </summary>
    private static TextEdit InsertTopLevelInOrder(SourceText source, YamlMapping mapping, IReadOnlyList<string> path, int rank, int unit, NewValue value, string nl)
    {
        var ranked = mapping.Entries.Select((e, i) => (Index: i, Rank: e.KeyText is { } k ? TemplateRank(k) : null)).Where(x => x.Rank is not null).ToList();
        var after = ranked.LastOrDefault(x => x.Rank < rank);
        var before = ranked.FirstOrDefault(x => x.Rank > rank);
        if (ranked.Count == 0 || (after.Rank is null && before.Rank is null))
        {
            return InsertEntry(source, mapping, path, 0, unit, value, nl);
        }

        var first = mapping.Entries[0].Key;
        var indent = Column(source, first.Start);
        var text = new string(' ', indent) + Chain(path, 0, indent, unit, value, nl);
        if (after.Rank is not null)
        {
            var entry = mapping.Entries[after.Index];
            var line = source.Lines[OwnedEnd(source, LastLineOfEntry(source, entry), Column(source, entry.Key.Start))];
            return line.TerminatorLength > 0
                ? new TextEdit(line.EndIncludingTerminator, 0, text + nl)
                : new TextEdit(line.End, 0, nl + text);
        }

        var keyLine = source.Lines[source.LineIndexOf(mapping.Entries[before.Index].Key.Start)];
        return new TextEdit(keyLine.Start, 0, text + nl);
    }

    private static List<TextEdit> ReplaceValue(SourceText source, YamlEntry entry, NewValue value, bool flow, int unit)
    {
        var current = entry.Value;
        if (value.Matches(current))
        {
            // Already says that: a write that changes nothing (F-RT-3).
            return [];
        }

        var colon = ColonAfter(source, entry.Key);
        var existing = YamlScalarValue.StyleOf(current);
        var token = value.Inline(flow, existing);
        if (token is null)
        {
            // A new block list under a null value: it goes on lines of its own.
            return AppendUnder(source, entry, value.Lines(Column(source, entry.Key.Start), unit));
        }

        var spaced = token.Length > 0 ? " " + token : "";
        if (current is YamlScalar { Style: YamlScalarStyle.Plain or YamlScalarStyle.SingleQuoted or YamlScalarStyle.DoubleQuoted } scalar)
        {
            if (scalar.Start == scalar.ContentEnd)
            {
                // `key:` with nothing after it, maybe with a comment.
                if (token.Length == 0)
                {
                    return [];
                }

                return flow ? [FlowFill(source, colon, token)] : [new TextEdit(colon + 1, 0, spaced)];
            }

            // Keep the spacing before the value and any comment after it.
            return token.Length > 0
                ? [new TextEdit(scalar.Start, scalar.ContentEnd - scalar.Start, token)]
                : [new TextEdit(colon + 1, scalar.ContentEnd - colon - 1, "")];
        }

        var end = current.ContentEnd;
        if (current is YamlMapping { IsFlow: false } or YamlSequence { IsFlow: false })
        {
            // A block collection is replaced whole, with any comments inside
            // it and one on its last line: they belong to the value being
            // replaced. Comment lines after it, at a lower indent, stay.
            end = source.Lines[LineIndexOfEnd(source, current)].End;
        }

        return [new TextEdit(colon + 1, end - colon - 1, spaced)];
    }

    /// <summary>Replaces list item <paramref name="k"/> with a scalar.</summary>
    private static List<TextEdit> ReplaceItem(SourceText source, YamlSequence sequence, int k, NewValue value)
    {
        var item = sequence.Items[k];
        if (value.Matches(item))
        {
            return [];
        }

        var token = value.Inline(sequence.IsFlow, YamlScalarValue.StyleOf(item))
            ?? throw new InvalidOperationException("Only a scalar can replace a list item.");
        if (token.Length == 0)
        {
            // A null item is written `- ~`, so the dash isn't left on its own.
            token = "~";
        }

        if (item is YamlScalar { Style: YamlScalarStyle.Plain or YamlScalarStyle.SingleQuoted or YamlScalarStyle.DoubleQuoted } scalar)
        {
            if (scalar.Start == scalar.ContentEnd)
            {
                var spaced = scalar.Start > 0 && source.Text[scalar.Start - 1] == '-' ? " " + token : token;
                return [new TextEdit(scalar.Start, 0, spaced)];
            }

            return [new TextEdit(scalar.Start, scalar.ContentEnd - scalar.Start, token)];
        }

        var end = item.ContentEnd;
        if (item is YamlMapping { IsFlow: false } or YamlSequence { IsFlow: false })
        {
            end = source.Lines[LineIndexOfEnd(source, item)].End;
        }

        return [new TextEdit(item.Start, end - item.Start, token)];
    }

    private static List<TextEdit> FillEmptyParent(SourceText source, YamlEntry entry, IReadOnlyList<string> path, int from, int unit, NewValue value, string nl)
    {
        var indent = Column(source, entry.Key.Start) + unit;
        return AppendUnder(source, entry, new string(' ', indent) + Chain(path, from, indent, unit, value, nl));
    }

    /// <summary>
    /// Puts <paramref name="lines"/> (already indented) on new lines under a
    /// key whose value is empty: <c>~</c> or <c>{}</c> is dropped, and a
    /// comment after it stays on the key's line.
    /// </summary>
    private static List<TextEdit> AppendUnder(SourceText source, YamlEntry entry, string lines)
    {
        var colon = ColonAfter(source, entry.Key);
        var edits = new List<TextEdit>();
        var current = entry.Value;
        if (current.ContentEnd > current.Start)
        {
            edits.Add(new TextEdit(colon + 1, current.ContentEnd - colon - 1, ""));
        }

        var line = source.Lines[source.LineIndexOf(Math.Max(colon, current.ContentEnd - 1))];
        edits.Add(new TextEdit(line.End, 0, source.Newline + lines));
        return edits;
    }

    /// <summary>Fills an empty value inside a flow mapping with a new flow mapping for <c>path[from..]</c>.</summary>
    private static TextEdit FillFlowEmpty(SourceText source, YamlEntry entry, IReadOnlyList<string> path, int from, NewValue value)
    {
        var text = "{ " + FlowChain(path, from, value) + " }";
        var current = entry.Value;
        if (current is YamlMapping empty)
        {
            if (source.Text.AsSpan(empty.Start, empty.ContentEnd - empty.Start).Contains('#'))
            {
                throw new NotSupportedException($"The empty mapping '{entry.KeyText}' at line {empty.Line} holds a comment, so it can't be filled in place.");
            }

            return new TextEdit(empty.Start, empty.ContentEnd - empty.Start, text);
        }

        return current.ContentEnd > current.Start
            ? new TextEdit(current.Start, current.ContentEnd - current.Start, text)
            : FlowFill(source, ColonAfter(source, entry.Key), text);
    }

    /// <summary>Writes <paramref name="token"/> after an empty <c>key:</c> in a flow mapping.</summary>
    private static TextEdit FlowFill(SourceText source, int colon, string token)
    {
        var text = source.Text;
        var p = colon + 1;
        while (p < text.Length && text[p] is ' ' or '\t')
        {
            p++;
        }

        // `{ a: , b }` becomes `{ a: x, b }`; `{ a: }` becomes `{ a: x }`.
        return p < text.Length && text[p] == ','
            ? new TextEdit(p, 0, (p == colon + 1 ? " " : "") + token)
            : new TextEdit(colon + 1, 0, " " + token);
    }

    private static List<TextEdit> BlockInsertItem(SourceText source, YamlSequence sequence, YamlValue item, int at)
    {
        var text = source.Text;
        var nl = source.Newline;
        var count = sequence.Items.Count;
        var reference = sequence.Items[at < count ? at : count - 1];
        var like = sequence.Items[at > 0 ? at - 1 : 0];
        var (dash, lineStart) = Dash(source, reference);
        var gap = reference.Start > dash + 1 && IsSpaces(text, dash + 1, reference.Start) ? text[(dash + 1)..reference.Start] : " ";
        var keyColumn = dash - lineStart + 1 + gap.Length;
        var body = item switch
        {
            YamlMappingValue { Entries.Count: > 0 } mapping when like is YamlMapping { IsFlow: false } block =>
                BlockMapping(mapping, block, keyColumn, nl, source),
            YamlScalarValue scalar => NonEmpty(scalar.Render(YamlScalarValue.StyleOf(like), flow: false)),
            _ => item.RenderFlow(like, source),
        };
        var newLine = text[lineStart..dash] + "-" + gap + body;

        if (at == 0)
        {
            // Before the comment lines right above the first item: they belong to it.
            var first = source.LineIndexOf(dash);
            while (first > 0 && IsCommentLine(source, first - 1))
            {
                first--;
            }

            return [new TextEdit(source.Lines[first].Start, 0, newLine + nl)];
        }

        // After the item before it and the comments indented under it, so
        // comment lines above the next item stay with that item.
        var previous = sequence.Items[at - 1];
        var (previousDash, previousLineStart) = Dash(source, previous);
        var line = source.Lines[OwnedEnd(source, LineIndexOfEnd(source, previous), previousDash - previousLineStart)];
        return line.TerminatorLength > 0
            ? [new TextEdit(line.EndIncludingTerminator, 0, newLine + nl)]
            : [new TextEdit(line.End, 0, nl + newLine)];

        static string NonEmpty(string token) => token.Length > 0 ? token : "~";
    }

    /// <summary>True if line <paramref name="index"/> holds only a YAML comment.</summary>
    private static bool IsCommentLine(SourceText source, int index) =>
        source.LineText(index).TrimStart(' ', '\t').StartsWith('#');

    private static bool IsBlankLine(SourceText source, int index) =>
        index >= 0 && index < source.Lines.Count && source.Lines[index].TerminatorLength > 0
        && IsSpaces(source.Text, source.Lines[index].Start, source.Lines[index].End);

    /// <summary>
    /// The last line that belongs to a block item or entry ending on
    /// <paramref name="lastLine"/>: that line, then any comment lines right
    /// after it indented deeper than <paramref name="column"/> (its dash or
    /// key), which are written under it.
    /// </summary>
    private static int OwnedEnd(SourceText source, int lastLine, int column)
    {
        var i = lastLine;
        while (i + 1 < source.Lines.Count && IsCommentLine(source, i + 1))
        {
            var lineText = source.LineText(i + 1);
            if (lineText.Length - lineText.TrimStart(' ', '\t').Length <= column)
            {
                break;
            }

            i++;
        }

        return i;
    }

    /// <summary>
    /// Deletes the lines of a block item or entry, with the comments
    /// indented under it. If it sat between two blank lines, one of them
    /// goes too, so blank lines don't pile up.
    /// </summary>
    private static TextEdit DeleteOwnedLines(SourceText source, int first, int last, int column)
    {
        last = OwnedEnd(source, last, column);
        if (IsBlankLine(source, first - 1) && IsBlankLine(source, last + 1))
        {
            last++;
        }

        return DeleteLines(source, first, last);
    }

    /// <summary>A new list item written as a block mapping, like its neighbor:
    /// <c>value: x</c> after the dash, then one key per line.</summary>
    private static string BlockMapping(YamlMappingValue mapping, YamlMapping like, int keyColumn, string nl, SourceText source)
    {
        var lines = mapping.Entries.Select(e =>
        {
            var neighbor = like[e.Key];
            var token = e.Value is YamlScalarValue scalar
                ? scalar.Render(YamlScalarValue.StyleOf(neighbor), flow: false)
                : e.Value.RenderFlow(neighbor, source);
            return token.Length > 0 ? e.Key + ": " + token : e.Key + ":";
        });
        return string.Join(nl + new string(' ', keyColumn), lines);
    }

    /// <param name="emptied">What the list becomes if its last item goes.</param>
    /// <param name="holder">The entry whose value the list is, or null for a
    /// list that's an item of another list. For <see cref="EmptiedList.EmptyList"/>,
    /// <c>[]</c> is written after that key's colon, before any comment on its line.</param>
    private static List<TextEdit> BlockRemoveItem(SourceText source, YamlSequence sequence, int k, EmptiedList emptied, YamlEntry? holder)
    {
        var item = sequence.Items[k];
        var (dash, lineStart) = Dash(source, item);
        var delete = DeleteOwnedLines(source, source.LineIndexOf(dash), LineIndexOfEnd(source, item), dash - lineStart);
        if (sequence.Items.Count > 1 || emptied == EmptiedList.Null)
        {
            return [delete];
        }

        if (holder is null)
        {
            throw new NotSupportedException($"The list at line {sequence.Line} is an item of another list, not the value of a key, so it can't be left as [] in place.");
        }

        var colon = ColonAfter(source, holder.Key);
        return [delete, new TextEdit(colon + 1, 0, " []")];
    }

    private static List<TextEdit> BlockRemoveEntry(SourceText source, YamlMapping mapping, int k)
    {
        var text = source.Text;
        var entry = mapping.Entries[k];

        // An explicit key (`?` then `: value` on another line) is refused here.
        ColonAfter(source, entry.Key);
        var keyLine = source.LineIndexOf(entry.Key.Start);
        var lastLine = LastLineOfEntry(source, entry);
        var keyLineStart = source.Lines[keyLine].Start;
        if (IsSpaces(text, keyLineStart, entry.Key.Start))
        {
            return [DeleteOwnedLines(source, keyLine, lastLine, entry.Key.Start - keyLineStart)];
        }

        // Something before the key on its line: a list item's dash
        // (`- key: value`), or `?` for an explicit key.
        var before = entry.Key.Start - 1;
        while (before > keyLineStart && text[before] is ' ' or '\t')
        {
            before--;
        }

        if (text[before] != '-' || !IsSpaces(text, keyLineStart, before))
        {
            throw new NotSupportedException($"'{entry.KeyText}' at line {entry.Key.Line} isn't written as `key: value` at the start of its line (an explicit `? key`, for example), so it can't be removed in place.");
        }

        // The first key of a list item: the next key moves up after the
        // dash, unless it's the only key.
        if (mapping.Entries.Count == 1)
        {
            throw new NotSupportedException($"'{entry.KeyText}' at line {entry.Key.Line} is the only key of a list item. Remove the item instead.");
        }

        var next = mapping.Entries[1].Key;
        var nextLine = source.LineIndexOf(next.Start);
        if (!IsSpaces(text, source.Lines[nextLine].Start, next.Start) || nextLine <= lastLine)
        {
            throw new NotSupportedException($"The key after '{entry.KeyText}' at line {entry.Key.Line} isn't on a line of its own, so it can't move up in place.");
        }

        for (var i = lastLine + 1; i < nextLine; i++)
        {
            if (!IsSpaces(text, source.Lines[i].Start, source.Lines[i].End))
            {
                throw new NotSupportedException($"A comment sits between '{entry.KeyText}' at line {entry.Key.Line} and the next key, so the key can't be removed without moving it.");
            }
        }

        return [new TextEdit(entry.Key.Start, next.Start - entry.Key.Start, "")];
    }

    /// <summary>The last line of an entry: its value's, or the key's for an empty value.</summary>
    private static int LastLineOfEntry(SourceText source, YamlEntry entry)
    {
        var value = entry.Value;
        var offset = value.ContentEnd > value.Start ? Math.Max(value.LastLineOffset, entry.Key.ContentEnd) : entry.Key.ContentEnd;
        return source.LineIndexOf(offset);
    }

    /// <summary>Deletes whole lines <paramref name="first"/> to <paramref name="last"/>.
    /// At the end of a file with no final newline, the line break before them
    /// goes instead, so the file still has none (F-ENC-4).</summary>
    private static TextEdit DeleteLines(SourceText source, int first, int last)
    {
        var start = source.Lines[first];
        var end = source.Lines[last];
        if (start.Start == 0 && end.TerminatorLength > 0 && end.EndIncludingTerminator == source.Text.Length)
        {
            // Emptying the whole file: its last line break stays, so it still
            // ends with a newline.
            return new TextEdit(0, end.End, "");
        }

        if (end.TerminatorLength > 0 || first == 0)
        {
            return new TextEdit(start.Start, end.EndIncludingTerminator - start.Start, "");
        }

        var before = source.Lines[first - 1].End;
        return new TextEdit(before, end.End - before, "");
    }

    /// <summary>The dash of a block list item, and the start of its line. The
    /// dash must be the first thing on the line.</summary>
    private static (int Dash, int LineStart) Dash(SourceText source, YamlNode item)
    {
        var text = source.Text;
        var i = item.Start - 1;
        while (i >= 0 && text[i] is ' ' or '\t')
        {
            i--;
        }

        if (i >= 0 && text[i] == '-')
        {
            var line = source.Lines[source.LineIndexOf(i)];
            if (IsSpaces(text, line.Start, i))
            {
                return (i, line.Start);
            }
        }

        throw new NotSupportedException($"The list item at line {item.Line} isn't written as '- item' at the start of its line, so it can't be edited in place.");
    }

    private static bool IsSpaces(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The offset of the colon that follows a mapping key.</summary>
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

    /// <summary>What an edit writes at the end of its path.</summary>
    private abstract class NewValue
    {
        public abstract bool Matches(YamlNode? node);

        /// <summary>The value as one token for <c>key: token</c> (empty for
        /// a block null), or null if it needs lines of its own.</summary>
        public abstract string? Inline(bool flow, YamlScalarStyle? existing);

        /// <summary>The value's own lines under a key at <paramref name="keyIndent"/>, indented.</summary>
        public virtual string Lines(int keyIndent, int unit) => throw new InvalidOperationException("The value fits on the key's line.");
    }

    private sealed class ScalarNew(YamlScalarValue value) : NewValue
    {
        public override bool Matches(YamlNode? node) => value.Matches(node);

        public override string? Inline(bool flow, YamlScalarStyle? existing) => value.Render(existing, flow);
    }

    /// <summary>
    /// A new list or mapping (<see cref="SetAny"/>): flow on the key's line,
    /// or, for a non-empty list of mappings under a block key, one
    /// <c>- { ... }</c> line per item.
    /// </summary>
    private sealed class AnyNew(YamlValue value, string newline) : NewValue
    {
        private bool IsListOfMappings => value is YamlSequenceValue { Items.Count: > 0 } list && list.Items.All(i => i is YamlMappingValue);

        public override bool Matches(YamlNode? node) => value.MatchesAsWritten(node);

        public override string? Inline(bool flow, YamlScalarStyle? existing) =>
            !flow && IsListOfMappings ? null : value.RenderFlow(null, null);

        public override string Lines(int keyIndent, int unit) =>
            string.Join(newline, ((YamlSequenceValue)value).Items.Select(i => new string(' ', keyIndent + unit) + "- " + i.RenderFlow(null, null)));
    }

    /// <summary>
    /// A new list holding one item. A scalar list is written as a flow list
    /// (<c>aliases: [Lucy]</c>). A list of mappings, such as a temporal list,
    /// is a block list of flow mappings, as in format.md §7.3, except inside
    /// a flow mapping, where it stays flow.
    /// </summary>
    private sealed class ListNew(YamlValue item) : NewValue
    {
        public override bool Matches(YamlNode? node) => false;

        public override string? Inline(bool flow, YamlScalarStyle? existing) =>
            !flow && item is YamlMappingValue ? null : "[" + item.RenderFlow(null, null) + "]";

        public override string Lines(int keyIndent, int unit) => new string(' ', keyIndent + unit) + "- " + item.RenderFlow(null, null);
    }
}
