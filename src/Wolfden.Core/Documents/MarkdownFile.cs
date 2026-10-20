using System.Text.RegularExpressions;
using Wolfden.Core.Text;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Documents;

/// <summary>
/// A markdown file with optional YAML frontmatter: an entity, a container
/// file, an outline, or a template. It keeps the original text and records
/// where each part sits, so it writes back byte for byte (format.md §12).
/// </summary>
public sealed partial class MarkdownFile : DenFile
{
    private readonly bool _requireFrontmatter;
    private readonly YamlBlock? _frontmatter;
    private readonly int _frontmatterEnd;
    private readonly int _bodyStart;
    private readonly int _leadEnd;
    private readonly IReadOnlyList<Section> _sections = [];
    private readonly Diagnostic? _missingFrontmatter;

    private MarkdownFile(byte[] bytes, bool requireFrontmatter)
        : base(bytes)
    {
        _requireFrontmatter = requireFrontmatter;
        if (Source is null)
        {
            return;
        }

        var diagnostics = new List<Diagnostic>();
        var lines = Source.Lines;
        if (IsFence(0))
        {
            var close = -1;
            for (var i = 1; i < lines.Count; i++)
            {
                if (IsFence(i))
                {
                    close = i;
                    break;
                }
            }

            if (close < 0)
            {
                diagnostics.Add(new Diagnostic("F-FM-1", DiagnosticSeverity.Error, 1,
                    "The frontmatter never closes: add a line that is exactly '---' after it."));
            }
            else
            {
                HasFrontmatter = true;
                _frontmatter = YamlBlock.Parse(Source, lines[1].Start, lines[close].Start);
                diagnostics.AddRange(_frontmatter.Diagnostics);
                _frontmatterEnd = lines[close].EndIncludingTerminator;
                _bodyStart = _frontmatterEnd;
            }
        }
        else if (requireFrontmatter)
        {
            _missingFrontmatter = new Diagnostic("F-FM-1", DiagnosticSeverity.Error, 1,
                "The file has no frontmatter: it must start with a line that is exactly '---'.");
            diagnostics.Add(_missingFrontmatter);
        }

        var body = BodyParser.Parse(this, Source, _bodyStart, diagnostics);
        _sections = body.Sections;
        _leadEnd = body.LeadEnd;
        Title = body.Title;
        AddDiagnostics(diagnostics);
        SortDiagnostics();

        bool IsFence(int index) =>
            index < lines.Count && Fence().IsMatch(Source.LineText(index)) &&
            !(index == lines.Count - 1 && lines[index].Length == 0);
    }

    /// <summary>True if the file starts with closed frontmatter (format.md F-FM-1).</summary>
    public bool HasFrontmatter { get; }

    /// <summary>
    /// The frontmatter's top-level mapping, or null if there's no
    /// frontmatter, it's empty, or it isn't a valid mapping.
    /// </summary>
    public YamlMapping? Frontmatter => _frontmatter?.RootMapping;

    /// <summary>The frontmatter as written, including its <c>---</c> lines,
    /// or an empty string.</summary>
    public string FrontmatterText => IsReadable ? Text[.._frontmatterEnd] : "";

    /// <summary>The body: everything after the frontmatter's closing line (F-FM-2).</summary>
    public string Body => IsReadable ? Text[_bodyStart..] : "";

    /// <summary>Offset of the body's first character in <see cref="DenFile.Text"/>.</summary>
    internal int BodyStart => _bodyStart;

    /// <summary>The lead: the body before the first level-2 heading (F-SEC-2).</summary>
    public string Lead => IsReadable ? Text[_bodyStart.._leadEnd] : "";

    /// <summary>The 1-based line where the body, and so the lead, starts.</summary>
    public int LeadLine => Source is { } source ? source.LineIndexOf(_bodyStart) + 1 : 1;

    /// <summary>The first level-1 heading in the lead, the display title, if any.</summary>
    public string? Title { get; }

    /// <summary>Every section in file order, subsections included.</summary>
    public IReadOnlyList<Section> Sections => _sections;

    /// <summary>The level-2 sections, in file order.</summary>
    public IEnumerable<Section> TopLevelSections => _sections.Where(s => s.Parent is null);

    /// <summary>
    /// Parses a markdown file. Never throws for bad content: problems come
    /// back as <see cref="DenFile.Diagnostics"/>.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="requireFrontmatter">True for container and entity files, where
    /// missing frontmatter is an F-FM-1 error.</param>
    public static MarkdownFile Parse(ReadOnlySpan<byte> bytes, bool requireFrontmatter = false) =>
        new(bytes.ToArray(), requireFrontmatter);

    /// <summary>The frontmatter value at <paramref name="path"/>, or null.</summary>
    public YamlNode? GetFrontmatterValue(params IReadOnlyList<string> path) => Find(Frontmatter, path);

    /// <summary>
    /// Sets a top-level frontmatter key, adding it after the existing keys if
    /// it's missing (format.md F-RT-4). Only that value's bytes change.
    /// </summary>
    public MarkdownFile WithFrontmatterValue(string key, YamlScalarValue value) =>
        WithFrontmatterValue([key], value);

    /// <summary>Sets a field under <c>fields:</c>, creating <c>fields:</c> if needed.</summary>
    public MarkdownFile WithField(string field, YamlScalarValue value) =>
        WithFrontmatterValue(["fields", field], value);

    /// <summary>
    /// Sets the scalar at <paramref name="path"/>: a top-level key, then keys
    /// of nested block mappings. Missing keys are added after their siblings;
    /// frontmatter is added if the file has none. Replacing a scalar keeps
    /// the spacing before it and a comment after it. Replacing a block list
    /// or mapping replaces it whole, including any comments inside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file can't be edited, or a key on the path isn't a mapping.</exception>
    /// <exception cref="NotSupportedException">The edit would have to change a flow mapping.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithFrontmatterValue(IReadOnlyList<string> path, YamlScalarValue value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        // Adding frontmatter to a file that has none fixes F-FM-1, so that
        // error doesn't block this edit.
        EnsureEditable(_frontmatter is null ? _missingFrontmatter : null);

        List<TextEdit> edits;
        if (_frontmatter is null)
        {
            // New frontmatter goes at the very start, after any BOM.
            var nl = Source!.Newline;
            var empty = YamlBlock.Parse(Source, 0, 0);
            var entry = YamlEditor.SetScalar(Source, empty, path, value).Single().NewText;
            edits = [new TextEdit(0, 0, "---" + nl + entry + "---" + nl)];
        }
        else
        {
            edits = YamlEditor.SetScalar(Source!, _frontmatter, path, value);
        }

        if (edits.Count == 0)
        {
            return this;
        }

        var result = Reparse(edits);
        if (result.Body != Body || !result.CanEdit || !YamlTreeCheck.SameExcept(_frontmatter?.Root, result._frontmatter?.Root, path, value))
        {
            throw new EditVerificationException($"Setting '{string.Join('.', path)}' didn't read back as written, or changed another value, so the edit was refused.");
        }

        return result;
    }

    /// <summary>
    /// Gives the file an <c>id</c> (format.md F-ID-6): it replaces an empty
    /// <c>id:</c>, or else adds <c>id: &lt;id&gt;</c> as the first
    /// frontmatter key, after any comment lines that open the frontmatter,
    /// at the indent of the other top-level keys. Nothing else changes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> isn't a valid id (F-ID-1).</exception>
    /// <exception cref="InvalidOperationException">The file can't be edited, has no frontmatter, or already has an id.</exception>
    /// <exception cref="NotSupportedException">The frontmatter is a flow mapping (<c>{ name: x }</c>).</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Structure.Keys.IsId(id))
        {
            throw new ArgumentException($"'{id}' isn't a valid id.", nameof(id));
        }

        EnsureEditable();
        if (_frontmatter is null)
        {
            throw new InvalidOperationException("The file has no frontmatter to add an id to.");
        }

        var value = YamlScalarValue.String(id);
        switch (_frontmatter.Root)
        {
            case null:
            case YamlMapping { IsFlow: false } m when m.Find("id") is { Value: YamlScalar { IsNull: true } }:
                // No keys yet (maybe comment lines), or an empty `id:`: set it in place.
                return WithFrontmatterValue(["id"], value);
            case YamlMapping { IsFlow: false } m when m.ContainsKey("id"):
                throw new InvalidOperationException("The file already has an id.");
            case YamlMapping { IsFlow: true }:
                throw new NotSupportedException("The frontmatter is a flow mapping, so an id can't be added as its first key.");
            case YamlMapping m:
                var source = Source!;
                var first = m.Entries[0].Key;
                var line = source.Lines[source.LineIndexOf(first.Start)];
                var indent = new string(' ', first.Start - line.Start);
                var result = Reparse([new TextEdit(line.Start, 0, $"{indent}id: {value.Render(null)}{source.Newline}")]);
                if (result.Body != Body || !result.CanEdit || !YamlTreeCheck.SameWithFirstKey(m, result.Frontmatter, "id", value))
                {
                    throw new EditVerificationException("Adding the id didn't read back as written, or changed another value, so the edit was refused.");
                }

                return result;
            default:
                throw new InvalidOperationException("The frontmatter isn't a mapping.");
        }
    }

    /// <summary>
    /// Replaces a section's own text (<see cref="Section.Body"/>): what
    /// follows its heading and section comment, up to the next heading. The
    /// heading, comment, subsections, and the blank lines around the old
    /// text are kept. Blank lines at the start and end of
    /// <paramref name="body"/> are dropped, so <c>"a\n\n\n"</c> is written as
    /// <c>a</c> and the old spacing around the text stays. Line breaks in
    /// <paramref name="body"/> are CR, LF, or CRLF, and are converted to the
    /// file's own; other characters, such as U+2028, are written as given.
    /// </summary>
    /// <exception cref="ArgumentException">The new text would add or change a heading or section comment.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithSectionBody(Section section, string body)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(body);
        EnsureEditable();
        if (!ReferenceEquals(section.Owner, this))
        {
            throw new ArgumentException("The section belongs to a different file.", nameof(section));
        }

        var source = Source!;
        var nl = source.Newline;
        var newLines = SplitLines(body);
        var start = section.BodyStart;
        var end = section.OwnEnd;

        int first = -1, last = -1;
        if (end > start)
        {
            for (var i = source.LineIndexOf(start); i < source.Lines.Count && source.Lines[i].Start < end; i++)
            {
                if (!IsBlank(source.LineText(i)))
                {
                    first = first < 0 ? i : first;
                    last = i;
                }
            }
        }

        TextEdit edit;
        if (first < 0)
        {
            if (newLines.Length == 0)
            {
                return this;
            }

            // No text yet: add it right under the heading, before any blank lines.
            var atLineStart = start == 0 || source.Text[start - 1] is '\n' or '\r';
            edit = atLineStart
                ? new TextEdit(start, 0, string.Join(nl, newLines) + nl)
                : new TextEdit(start, 0, nl + string.Join(nl, newLines));
        }
        else
        {
            var contentStart = source.Lines[first].Start;
            var lastLine = source.Lines[last];
            var hasTerminator = lastLine.TerminatorLength > 0;
            if (newLines.Length > 0)
            {
                edit = new TextEdit(contentStart, lastLine.EndIncludingTerminator - contentStart,
                    string.Join(nl, newLines) + (hasTerminator ? nl : ""));
            }
            else if (hasTerminator)
            {
                edit = new TextEdit(contentStart, lastLine.EndIncludingTerminator - contentStart, "");
            }
            else
            {
                // Clearing text at the end of a file with no final newline:
                // drop the line breaks before it too, so the file still has
                // no final newline (F-ENC-4).
                var from = source.Lines[source.LineIndexOf(start - 1)].End;
                edit = new TextEdit(from, lastLine.End - from, "");
            }
        }

        var result = Reparse([edit]);
        if (result.FrontmatterText != FrontmatterText || result.Lead != Lead)
        {
            throw new EditVerificationException("Replacing a section's text changed the frontmatter or lead, so the edit was refused.");
        }

        if (!SameOutline(result, 0))
        {
            throw new ArgumentException("The new text would add or change a heading or section comment. Edit one section at a time.", nameof(body));
        }

        for (var i = 0; i < Sections.Count; i++)
        {
            var expected = i == section.Index ? string.Join(nl, newLines) : TrimBlankLines(Sections[i].Body);
            if (TrimBlankLines(result.Sections[i].Body) != expected)
            {
                throw new EditVerificationException($"The text of '{result.Sections[i].Name}' didn't read back as written, so the edit was refused.");
            }
        }

        return result;
    }

    /// <summary>
    /// Adds a section at the end of the file: a heading of
    /// <paramref name="level"/> and its text, after a blank line. A file
    /// with no final newline still has none afterwards (F-ENC-4).
    /// </summary>
    /// <exception cref="ArgumentException">The name or text wouldn't read back as one new section, or a
    /// subsection has no level-2 section to go under.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithNewSection(string name, string body = "", int level = 2)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 6);
        EnsureEditable();
        if (name.Length == 0 || name != name.Trim() || name.Contains('\n') || name.Contains('\r'))
        {
            throw new ArgumentException("A section name is one line of text, with no leading or trailing spaces.", nameof(name));
        }

        if (level > 2 && !Sections.Any())
        {
            // Before the first level-2 heading, everything is lead (F-SEC-2).
            throw new ArgumentException($"A level-{level} section needs a level-2 section before it.", nameof(level));
        }

        var source = Source!;
        var nl = source.Newline;
        var text = source.Text;
        var block = string.Join(nl, new[] { new string('#', level) + " " + name }.Concat(SplitLines(body)));

        string insert;
        if (text.Length == 0)
        {
            insert = block + nl;
        }
        else if (!source.EndsWithNewline)
        {
            insert = nl + nl + block;
        }
        else
        {
            var lastLine = source.LineText(source.Lines.Count - 2);
            insert = (IsBlank(lastLine) ? "" : nl) + block + nl;
        }

        var result = Reparse([new TextEdit(text.Length, 0, insert)]);
        if (!result.Text.StartsWith(text, StringComparison.Ordinal))
        {
            throw new EditVerificationException("Adding a section changed existing text, so the edit was refused.");
        }

        var added = result.Sections.Count == Sections.Count + 1 ? result.Sections[^1] : null;
        if (added is null || added.Name != name || added.HeadingLevel != level || !SameOutline(result, 1))
        {
            throw new ArgumentException($"'{name}' and its text wouldn't read back as one new section.", nameof(name));
        }

        if (TrimBlankLines(result.Slice(added.HeadingEnd, added.OwnEnd)) != string.Join(nl, SplitLines(body)))
        {
            throw new EditVerificationException($"The text of '{name}' didn't read back as written, so the edit was refused.");
        }

        return result;
    }

    private MarkdownFile Reparse(IEnumerable<TextEdit> edits) => new(ApplyEdits(edits), _requireFrontmatter);

    /// <summary>True if <paramref name="other"/> has this file's title,
    /// headings, and section comments, plus <paramref name="extra"/> more sections.</summary>
    private bool SameOutline(MarkdownFile other, int extra)
    {
        if (other.Title != Title || other.Sections.Count != Sections.Count + extra)
        {
            return false;
        }

        for (var i = 0; i < Sections.Count; i++)
        {
            var (a, b) = (Sections[i], other.Sections[i]);
            if (a.Name != b.Name || a.HeadingLevel != b.HeadingLevel || a.Comment?.Text != b.Comment?.Text)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits on CRLF, LF, and CR only, the line breaks CommonMark and the
    /// line table agree on, and drops blank lines at both ends.
    /// </summary>
    private static string[] SplitLines(string text)
    {
        var lines = LineBreak().Split(text);
        int first = 0, last = lines.Length - 1;
        while (first <= last && IsBlank(lines[first]))
        {
            first++;
        }

        while (last >= first && IsBlank(lines[last]))
        {
            last--;
        }

        return lines[first..(last + 1)];
    }

    /// <summary>The text with blank lines at both ends removed, keeping its own line breaks.</summary>
    private static string TrimBlankLines(string text)
    {
        var lines = LineBreak().Split(text);
        var breaks = LineBreak().Matches(text);
        int first = 0, last = lines.Length - 1;
        while (first <= last && IsBlank(lines[first]))
        {
            first++;
        }

        while (last >= first && IsBlank(lines[last]))
        {
            last--;
        }

        if (first > last)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(lines[first]);
        for (var i = first + 1; i <= last; i++)
        {
            sb.Append(breaks[i - 1].Value).Append(lines[i]);
        }

        return sb.ToString();
    }

    private static bool IsBlank(string line) => line.All(c => c is ' ' or '\t');

    [GeneratedRegex("^---[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex("\r\n|\r|\n", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();
}
