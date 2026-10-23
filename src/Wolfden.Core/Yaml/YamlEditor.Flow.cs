using Wolfden.Core.Text;

namespace Wolfden.Core.Yaml;

/// <summary>
/// Adding and removing one item of a flow collection (<c>[a, b]</c> or
/// <c>{ a: 1, b: 2 }</c>) in place. Comments can sit inside a flow
/// collection that spans lines, so an edit never deletes text between
/// items that holds one, and a comment on an item's own line goes with
/// that item.
/// </summary>
internal static partial class YamlEditor
{
    /// <summary>
    /// Inserts <paramref name="itemText"/> before item <paramref name="at"/>,
    /// or at the end. An item on a line of its own gets a line of its own;
    /// otherwise it goes on the same line, with the separator the
    /// collection already uses (<c>, </c> by default).
    /// </summary>
    private static List<TextEdit> FlowInsert(SourceText source, YamlNode collection, string itemText, int at)
    {
        var layout = FlowLayout.Of(source, collection);
        var text = source.Text;
        var nl = source.Newline;
        var items = layout.Items;
        var count = items.Count;
        if (count == 0)
        {
            var inner = text[(layout.Open + 1)..layout.Close];
            if (inner.Contains('#'))
            {
                throw new NotSupportedException($"The empty collection at line {collection.Line} holds a comment, so an item can't be added in place.");
            }

            var padded = inner.Length > 0 && inner.All(c => c == ' ');
            return [new TextEdit(layout.Open + 1, inner.Length, padded ? " " + itemText + " " : itemText)];
        }

        var reference = at < count ? at : count - 1;
        if (layout.OwnLine(reference))
        {
            if (at == count)
            {
                var last = count - 1;
                var comma = layout.CommaAfter(last);
                var line = source.Lines[source.LineIndexOf(items[last].End)];
                List<TextEdit> edits = [new TextEdit(line.EndIncludingTerminator, 0, layout.Indent(last) + itemText + (comma >= 0 ? "," : "") + nl)];
                if (comma < 0)
                {
                    edits.Add(new TextEdit(items[last].End, 0, ","));
                }

                return edits;
            }

            var newLine = layout.Indent(at) + itemText + "," + nl;
            return at > 0
                ? [new TextEdit(source.Lines[source.LineIndexOf(items[at - 1].End)].EndIncludingTerminator, 0, newLine)]
                : [new TextEdit(source.Lines[source.LineIndexOf(items[0].Start)].Start, 0, newLine)];
        }

        var separator = ", ";
        if (count >= 2)
        {
            var j = at == count ? count - 2 : Math.Min(at, count - 2);
            var gap = text[items[j].End..items[j + 1].Start];
            if (gap.IndexOfAny(['\n', '\r', '#']) < 0)
            {
                separator = gap;
            }
        }

        return at == count
            ? [new TextEdit(items[count - 1].End, 0, separator + itemText)]
            : [new TextEdit(items[at].Start, 0, itemText + separator)];
    }

    /// <summary>Removes item <paramref name="k"/>: its line, if it has one
    /// to itself, or else the item and one separator next to it.</summary>
    private static List<TextEdit> FlowRemove(SourceText source, YamlNode collection, int k)
    {
        var layout = FlowLayout.Of(source, collection);
        var items = layout.Items;
        var count = items.Count;
        var item = items[k];

        if (count == 1 && !layout.HasComment(layout.Open + 1, item.Start) && !layout.HasComment(item.End, layout.Close))
        {
            // The last item: an empty collection, `[]` or `{}`.
            return [new TextEdit(layout.Open + 1, layout.Close - layout.Open - 1, "")];
        }

        if (layout.OwnLine(k))
        {
            var first = source.Lines[source.LineIndexOf(item.Start)];
            var last = source.Lines[source.LineIndexOf(item.End)];
            List<TextEdit> edits = [new TextEdit(first.Start, last.EndIncludingTerminator - first.Start, "")];
            if (k == count - 1 && count > 1 && layout.CommaAfter(k) < 0)
            {
                // The item before is now the last one; it had a comma only
                // as a separator, so the comma goes too.
                edits.Add(new TextEdit(layout.CommaAfter(k - 1), 1, ""));
            }

            return edits;
        }

        if (count > 1)
        {
            if (k < count - 1 && !layout.HasComment(item.End, items[k + 1].Start))
            {
                return [new TextEdit(item.Start, items[k + 1].Start - item.Start, "")];
            }

            if (k == count - 1 && !layout.HasComment(items[k - 1].End, item.Start))
            {
                return [new TextEdit(items[k - 1].End, item.End - items[k - 1].End, "")];
            }
        }

        throw new NotSupportedException($"A comment sits next to the item at line {source.LineIndexOf(item.Start) + 1}, so the item can't be removed without deleting or moving the comment.");
    }

    /// <summary>Where a flow collection's brackets and items sit in the text.</summary>
    private sealed class FlowLayout
    {
        private readonly string _text;
        private readonly SourceText _source;

        private FlowLayout(SourceText source, int open, int close, List<(int Start, int End)> items)
        {
            _source = source;
            _text = source.Text;
            Open = open;
            Close = close;
            Items = items;
        }

        /// <summary>The offset of the opening bracket.</summary>
        public int Open { get; }

        /// <summary>The offset of the closing bracket.</summary>
        public int Close { get; }

        /// <summary>Each item's text: a list item, or a mapping entry from its key to the end of its value.</summary>
        public List<(int Start, int End)> Items { get; }

        public static FlowLayout Of(SourceText source, YamlNode collection)
        {
            var text = source.Text;
            var open = collection.Start;
            var close = collection.ContentEnd - 1;
            if (close <= open || text[open] is not ('[' or '{') || text[close] is not (']' or '}'))
            {
                throw new NotSupportedException($"The collection at line {collection.Line} isn't written with brackets, so it can't be edited in place.");
            }

            var items = collection switch
            {
                YamlSequence sequence => sequence.Items.Select(i => (i.Start, i.ContentEnd)).ToList(),
                YamlMapping mapping => mapping.Entries.Select(e => (e.Key.Start, EntryEnd(source, e))).ToList(),
                _ => [],
            };

            var previous = open;
            foreach (var (start, end) in items)
            {
                // A pair without braces (`[a: 1]`) has no position of its own.
                if (start <= previous || end < start || end > close)
                {
                    throw new NotSupportedException($"An item of the collection at line {collection.Line} can't be located, so it can't be edited in place.");
                }

                previous = end;
            }

            return new FlowLayout(source, open, close, items);
        }

        /// <summary>The offset of the comma after item <paramref name="k"/>, or -1 if the closing bracket comes next.</summary>
        public int CommaAfter(int k)
        {
            var p = Items[k].End;
            while (p < Close)
            {
                if (_text[p] is ' ' or '\t' or '\r' or '\n')
                {
                    p++;
                }
                else if (_text[p] == '#')
                {
                    while (p < Close && _text[p] is not ('\r' or '\n'))
                    {
                        p++;
                    }
                }
                else
                {
                    break;
                }
            }

            return p < Close && _text[p] == ',' ? p : -1;
        }

        /// <summary>True if a comment starts in [<paramref name="start"/>, <paramref name="end"/>),
        /// a stretch between items, where every <c>#</c> starts one.</summary>
        public bool HasComment(int start, int end) => _text.AsSpan(start, end - start).Contains('#');

        /// <summary>
        /// True if item <paramref name="k"/> has its lines to itself: only
        /// spaces before it, and after it only a comma and a comment.
        /// </summary>
        public bool OwnLine(int k)
        {
            var (start, end) = Items[k];
            if (!IsSpaces(_text, _source.Lines[_source.LineIndexOf(start)].Start, start))
            {
                return false;
            }

            var p = end;
            SkipSpaces(ref p);
            if (p < _text.Length && _text[p] == ',')
            {
                p++;
                SkipSpaces(ref p);
            }

            if (p < _text.Length && _text[p] == '#')
            {
                while (p < _text.Length && _text[p] is not ('\r' or '\n'))
                {
                    p++;
                }
            }

            return p < _text.Length && _text[p] is '\r' or '\n';
        }

        /// <summary>The spaces before item <paramref name="k"/> on its line.</summary>
        public string Indent(int k)
        {
            var start = Items[k].Start;
            var lineStart = _source.Lines[_source.LineIndexOf(start)].Start;
            return IsSpaces(_text, lineStart, start) ? _text[lineStart..start] : "";
        }

        private static int EntryEnd(SourceText source, YamlEntry entry)
        {
            if (entry.Value.ContentEnd > entry.Value.Start)
            {
                return entry.Value.ContentEnd;
            }

            // `{ a: }` or `{ a }`: the entry ends at its colon, or its key.
            var text = source.Text;
            var i = entry.Key.ContentEnd;
            while (i < text.Length && text[i] is ' ' or '\t')
            {
                i++;
            }

            return i < text.Length && text[i] == ':' ? i + 1 : entry.Key.ContentEnd;
        }

        private void SkipSpaces(ref int p)
        {
            while (p < _text.Length && _text[p] is ' ' or '\t')
            {
                p++;
            }
        }
    }
}
