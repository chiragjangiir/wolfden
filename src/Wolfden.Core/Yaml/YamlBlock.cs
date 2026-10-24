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
    /// <summary>Deeper nesting is reported as F-YAML-1, so a hostile file
    /// can't exhaust the stack.</summary>
    internal const int MaxDepth = 256;

    private readonly HashSet<string> _anchors = new(StringComparer.Ordinal);
    private readonly SourceText _source;
    private readonly List<Diagnostic> _diagnostics = [];

    /// <summary>How many flow collections (<c>[...]</c>, <c>{...}</c>) enclose the node being read.</summary>
    private int _flowDepth;

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
            Root = ReadNode(parser, 0);
            parser.MoveNext(); // past DocumentEnd
            if (parser.Current is DocumentStart second)
            {
                Error("F-YAML-1", Abs(second.Start), "Only one YAML document is allowed here.");
                Root = null;
                Failed = true;
                return;
            }

            if (Root is YamlScalar { Style: YamlScalarStyle.Plain, Text: "" })
            {
                // An explicit but empty document (`---` alone) holds nothing,
                // like empty frontmatter.
                Root = null;
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
        catch (TooDeepException ex)
        {
            Fail(LineOf(ex.Offset), $"values are nested more than {MaxDepth} levels deep.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // YamlDotNet throws InvalidOperationException and others for some
            // malformed input, such as an unclosed flow sequence. It's still a
            // parse failure, not a crash.
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

    private YamlNode ReadNode(IParser parser, int depth)
    {
        var current = parser.Current ?? throw new InvalidOperationException("Unexpected end of YAML events.");
        if (depth > MaxDepth)
        {
            throw new TooDeepException(Abs(current.Start));
        }

        if (current is NodeEvent node)
        {
            CheckProperties(node);
        }

        switch (current)
        {
            case AnchorAlias alias:
            {
                Error("F-YAML-4", Abs(alias.Start), $"Aliases (*{alias.Value}) aren't allowed.");
                if (!_anchors.Contains(alias.Value.Value))
                {
                    Error("F-YAML-1", Abs(alias.Start), $"The alias *{alias.Value} names no anchor before it.");
                }

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
                int? lastLine = null;
                if (style is YamlScalarStyle.Literal or YamlScalarStyle.Folded)
                {
                    if (IsKeepChomped(start))
                    {
                        // `|+` keeps its trailing blank lines: they're part of
                        // the value, so the content runs to the end mark, less
                        // the last line break.
                        if (end > start && _source.Text[end - 1] == '\n')
                        {
                            end--;
                        }

                        if (end > start && _source.Text[end - 1] == '\r')
                        {
                            end--;
                        }

                        lastLine = end;
                    }
                    else
                    {
                        // Otherwise the end mark lands after trailing blank
                        // lines that aren't part of the value; the content ends
                        // at the last non-blank character.
                        while (end > start && char.IsWhiteSpace(_source.Text[end - 1]))
                        {
                            end--;
                        }
                    }
                }

                if (style == YamlScalarStyle.Plain)
                {
                    CheckColonBeforeFlowIndicator(scalar.Value, start, end);
                }

                parser.MoveNext();
                return new YamlScalar(scalar.Value, style, LineOf(start), start, end, lastLine);
            }

            case SequenceStart sequenceStart:
            {
                var start = Abs(sequenceStart.Start);
                var isFlow = sequenceStart.Style == SequenceStyle.Flow;
                var items = new List<YamlNode>();
                parser.MoveNext();
                _flowDepth += isFlow ? 1 : 0;
                while (parser.Current is not SequenceEnd)
                {
                    items.Add(ReadNode(parser, depth + 1));
                }

                _flowDepth -= isFlow ? 1 : 0;

                var end = CollectionEnd(isFlow, (SequenceEnd)parser.Current, items.Count > 0 ? items[^1].ContentEnd : start);
                int? lastLine = !isFlow && items.Count > 0 ? items[^1].LastLineOffset : null;
                parser.MoveNext();
                return new YamlSequence(items, isFlow, LineOf(start), start, end, lastLine);
            }

            case MappingStart mappingStart:
            {
                var start = Abs(mappingStart.Start);
                var isFlow = mappingStart.Style == MappingStyle.Flow;
                var entries = new List<YamlEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                parser.MoveNext();
                _flowDepth += isFlow ? 1 : 0;
                while (parser.Current is not MappingEnd)
                {
                    var key = ReadNode(parser, depth + 1);
                    var value = ReadNode(parser, depth + 1);
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

                _flowDepth -= isFlow ? 1 : 0;

                var end = CollectionEnd(isFlow, (MappingEnd)parser.Current, entries.Count > 0 ? entries[^1].Value.ContentEnd : start);
                int? lastLine = !isFlow && entries.Count > 0 ? entries[^1].Value.LastLineOffset : null;
                parser.MoveNext();
                return new YamlMapping(entries, isFlow, LineOf(start), start, end, lastLine);
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

    /// <summary>True if the block scalar header at <paramref name="start"/> has the keep indicator (<c>+</c>).</summary>
    private bool IsKeepChomped(int start)
    {
        var text = _source.Text;
        for (var i = start + 1; i < text.Length && (text[i] is '+' or '-' || char.IsAsciiDigit(text[i])); i++)
        {
            if (text[i] == '+')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// F-YAML-1: inside a flow collection, YAML 1.2 ends a plain scalar
    /// before a <c>:</c> that runs straight into <c>}</c> or <c>]</c>, and
    /// takes the colon as a key separator: <c>{from:}</c> is the key
    /// <c>from</c> and <c>[b:]</c> holds the pair <c>{b: null}</c>.
    /// YamlDotNet keeps the colon in the scalar instead (<c>from:</c>,
    /// <c>b:</c>). Before a comma every reader, YamlDotNet included, already
    /// ends the scalar, so the scalar never ends with that colon and the
    /// check, which also looks at <c>,</c> in case that changes, doesn't
    /// fire. Wolfden can't read such a value the way other YAML readers do,
    /// so it's an error rather than a silent disagreement (#134). It's
    /// reported on the colon's line.
    /// </summary>
    private void CheckColonBeforeFlowIndicator(string value, int start, int end)
    {
        var text = _source.Text;
        if (_flowDepth > 0 && value.EndsWith(':') && end > start && text[end - 1] == ':'
            && end < RegionEnd && text[end] is ',' or '}' or ']')
        {
            var key = value[..^1].TrimEnd();
            Error("F-YAML-1", end - 1,
                $"'{value}' runs straight into '{text[end]}', which YAML readers don't all read the same way. " +
                $"For a key with an empty value, put a space after the colon ('{key}: {text[end]}'), or quote the text if the colon is part of it.");
        }
    }

    private void CheckProperties(NodeEvent node)
    {
        if (!node.Anchor.IsEmpty)
        {
            _anchors.Add(node.Anchor.Value);
            Error("F-YAML-4", Abs(node.Start), $"Anchors (&{node.Anchor}) aren't allowed.");
        }

        if (!node.Tag.IsEmpty)
        {
            Error("F-YAML-4", Abs(node.Start), $"Explicit tags ({node.Tag}) aren't allowed.");
        }
    }

    private sealed class TooDeepException(int offset) : Exception
    {
        public int Offset { get; } = offset;
    }
}
