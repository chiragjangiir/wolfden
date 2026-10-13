using Wolfden.Core.Documents;
using Wolfden.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YScalar = YamlDotNet.Core.Events.Scalar;

namespace Wolfden.Core.Yaml;

/// <summary>
/// One region of a file parsed as a single YAML document: a frontmatter
/// block or a whole <c>.yaml</c> file. It builds a read-only node tree that
/// remembers where each node sits in the text, so edits can replace just
/// those characters.
/// </summary>
internal sealed class YamlBlock
{
    private readonly SourceText _source;
    private readonly List<Diagnostic> _diagnostics = [];

    private YamlBlock(SourceText source, int start, int end)
    {
        _source = source;
        RegionStart = start;
        RegionEnd = end;
    }

    /// <summary>Offset of the region's first character.</summary>
    public int RegionStart { get; }

    /// <summary>Offset just after the region.</summary>
    public int RegionEnd { get; }

    /// <summary>The document's root, or null when the region holds no
    /// document (only blank lines and comments) or it failed to parse.</summary>
    public YamlNode? Root { get; private set; }

    /// <summary>True when the region failed to parse (F-YAML-1).</summary>
    public bool Failed { get; private set; }

    /// <summary>True when the region holds no YAML document at all.</summary>
    public bool IsEmpty => Root is null && !Failed;

    /// <summary>The root as a mapping, or an empty-document null.</summary>
    public YamlMapping? RootMapping => Root as YamlMapping;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public static YamlBlock Parse(SourceText source, int start, int end)
    {
        var block = new YamlBlock(source, start, end);
        block.ParseCore();
        return block;
    }

    private int LineOf(int offset) => _source.LineIndexOf(offset) + 1;

    private void Error(string rule, int offset, string message) =>
        _diagnostics.Add(new Diagnostic(rule, DiagnosticSeverity.Error, LineOf(offset), message));

    private void ParseCore()
    {
        var text = _source.Text.Substring(RegionStart, RegionEnd - RegionStart);
        try
        {
            var parser = new Parser(new Scanner(new StringReader(text), skipComments: true));
            parser.MoveNext(); // StreamStart
            parser.MoveNext();
            if (parser.Current is StreamEnd)
            {
                return;
            }

            parser.MoveNext(); // past DocumentStart
            Root = ReadNode(parser);
            parser.MoveNext(); // past DocumentEnd
            if (parser.Current is DocumentStart second)
            {
                Error("F-YAML-1", Abs(second.Start), "Only one YAML document is allowed here.");
                Root = null;
                Failed = true;
                return;
            }

            if (Root is not YamlMapping)
            {
                Error("F-YAML-2", Root.Start, "The top level must be a mapping of keys to values.");
            }
        }
        catch (YamlException ex)
        {
            Fail(_source.LineIndexOf(RegionStart) + (int)ex.Start.Line, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // YamlDotNet throws this for some malformed input, such as an
            // unclosed flow sequence. It's still a parse failure, not a crash.
            Fail(null, ex.Message);
        }
    }

    private void Fail(int? line, string message)
    {
        Root = null;
        Failed = true;
        _diagnostics.Add(new Diagnostic("F-YAML-1", DiagnosticSeverity.Error, line ?? LineOf(RegionStart),
            $"The YAML doesn't parse: {message}"));
    }

    private int Abs(Mark mark) => RegionStart + (int)mark.Index;

    private YamlNode ReadNode(IParser parser)
    {
        var current = parser.Current ?? throw new InvalidOperationException("Unexpected end of YAML events.");
        if (current is NodeEvent node)
        {
            CheckProperties(node);
        }

        switch (current)
        {
            case AnchorAlias alias:
            {
                Error("F-YAML-4", Abs(alias.Start), $"Aliases (*{alias.Value}) aren't allowed.");
                var start = Abs(alias.Start);
                var end = Abs(alias.End);
                parser.MoveNext();
                return new YamlScalar("*" + alias.Value, YamlScalarStyle.Plain, LineOf(start), start, end);
            }

            case YScalar scalar:
            {
                var start = Abs(scalar.Start);
                var end = Abs(scalar.End);
                var style = scalar.Style switch
                {
                    ScalarStyle.SingleQuoted => YamlScalarStyle.SingleQuoted,
                    ScalarStyle.DoubleQuoted => YamlScalarStyle.DoubleQuoted,
                    ScalarStyle.Literal => YamlScalarStyle.Literal,
                    ScalarStyle.Folded => YamlScalarStyle.Folded,
                    _ => YamlScalarStyle.Plain,
                };
                if (style is YamlScalarStyle.Literal or YamlScalarStyle.Folded)
                {
                    // A block scalar's end mark lands after its trailing
                    // blank lines; its content ends at the last non-blank.
                    while (end > start && char.IsWhiteSpace(_source.Text[end - 1]))
                    {
                        end--;
                    }
                }

                parser.MoveNext();
                return new YamlScalar(scalar.Value, style, LineOf(start), start, end);
            }

            case SequenceStart sequenceStart:
            {
                var start = Abs(sequenceStart.Start);
                var isFlow = sequenceStart.Style == SequenceStyle.Flow;
                var items = new List<YamlNode>();
                parser.MoveNext();
                while (parser.Current is not SequenceEnd)
                {
                    items.Add(ReadNode(parser));
                }

                var end = CollectionEnd(isFlow, (SequenceEnd)parser.Current, items.Count > 0 ? items[^1].ContentEnd : start);
                parser.MoveNext();
                return new YamlSequence(items, isFlow, LineOf(start), start, end);
            }

            case MappingStart mappingStart:
            {
                var start = Abs(mappingStart.Start);
                var isFlow = mappingStart.Style == MappingStyle.Flow;
                var entries = new List<YamlEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                parser.MoveNext();
                while (parser.Current is not MappingEnd)
                {
                    var key = ReadNode(parser);
                    var value = ReadNode(parser);
                    if (key is YamlScalar keyScalar)
                    {
                        if (keyScalar.Style == YamlScalarStyle.Plain && keyScalar.Text == "<<")
                        {
                            Error("F-YAML-4", key.Start, "Merge keys (<<) aren't allowed.");
                        }
                        else if (!seen.Add(keyScalar.Text))
                        {
                            Error("F-YAML-3", key.Start, $"The key '{keyScalar.Text}' is repeated.");
                        }
                    }

                    entries.Add(new YamlEntry(key, value));
                }

                var end = CollectionEnd(isFlow, (MappingEnd)parser.Current, entries.Count > 0 ? entries[^1].Value.ContentEnd : start);
                parser.MoveNext();
                return new YamlMapping(entries, isFlow, LineOf(start), start, end);
            }

            default:
                throw new InvalidOperationException($"Unexpected YAML event {current.GetType().Name}.");
        }
    }

    private int CollectionEnd(bool isFlow, ParsingEvent endEvent, int lastChildEnd)
    {
        if (!isFlow)
        {
            // A block collection's end mark is at the next token, past any
            // trailing comments; its content ends with its last child.
            return lastChildEnd;
        }

        // A flow collection's end event sits on its closing bracket.
        var at = Abs(endEvent.Start);
        return at < _source.Text.Length && _source.Text[at] is ']' or '}' ? at + 1 : Abs(endEvent.End);
    }

    private void CheckProperties(NodeEvent node)
    {
        if (!node.Anchor.IsEmpty)
        {
            Error("F-YAML-4", Abs(node.Start), $"Anchors (&{node.Anchor}) aren't allowed.");
        }

        if (!node.Tag.IsEmpty)
        {
            Error("F-YAML-4", Abs(node.Start), $"Explicit tags ({node.Tag}) aren't allowed.");
        }
    }
}
