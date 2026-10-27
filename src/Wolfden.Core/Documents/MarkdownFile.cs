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
    private readonly bool _isOutline;
    private readonly YamlBlock? _frontmatter;
    private readonly int _frontmatterEnd;
    private readonly int _bodyStart;
    private readonly int _leadEnd;
    private readonly (int Start, int End)? _titleSpan;
    private readonly IReadOnlyList<Section> _sections = [];
    private readonly Diagnostic? _missingFrontmatter;

    private MarkdownFile(byte[] bytes, bool requireFrontmatter, bool isOutline = false)
        : base(bytes)
    {
        _requireFrontmatter = requireFrontmatter;
        _isOutline = isOutline;
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

        var body = BodyParser.Parse(this, Source, _bodyStart, diagnostics, isOutline);
        _sections = body.Sections;
        _leadEnd = body.LeadEnd;
        Title = body.Title;
        _titleSpan = body.TitleSpan;
        AddDiagnostics(diagnostics);
        SortDiagnostics();

        bool IsFence(int index) =>
            index < lines.Count && Fence().IsMatch(Source.LineText(index)) &&
            !(index == lines.Count - 1 && lines[index].Length == 0);
    }

    /// <summary>True if the file starts with closed frontmatter (format.md F-FM-1).</summary>
    public bool HasFrontmatter { get; }

    /// <summary>
    /// True if the file was parsed as a book's <c>outline.md</c>
    /// (<see cref="ParseOutline"/>), where wolfden comments do nothing
    /// (format.md F-SEC-10), so its sections never have a comment.
    /// </summary>
    public bool IsOutline => _isOutline;

    /// <summary>
    /// The frontmatter's top-level mapping, or null if there's no
    /// frontmatter, it's empty, or it isn't a valid mapping.
    /// </summary>
    public YamlMapping? Frontmatter => _frontmatter?.RootMapping;

    /// <summary>The text of every YAML comment in the frontmatter, in order, without its <c>#</c>.</summary>
    public IReadOnlyList<string> FrontmatterComments => _frontmatter?.Comments() ?? [];

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

    /// <summary>
    /// Parses a book's <c>outline.md</c> (format.md §11). Its frontmatter is
    /// optional (F-OUT-1), and wolfden comments do nothing in it (F-SEC-10):
    /// each one, well-formed or not, is a warning and never an error, so a
    /// bad <c>from=</c> or <c>status=</c> in one doesn't exclude the outline
    /// (F-GEN-2, #80). Its headings still split it into sections, but no
    /// section has a comment, and a level-1 heading anywhere is fine (F-OUT-3).
    /// </summary>
    public static MarkdownFile ParseOutline(ReadOnlySpan<byte> bytes) =>
        new(bytes.ToArray(), requireFrontmatter: false, isOutline: true);

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
    /// Sets the scalar at <paramref name="path"/>. A path names a top-level
    /// key, then keys of nested mappings, block or flow, and item numbers
    /// from 0 in lists, so <c>["fields", "died", "from"]</c> changes only
    /// <c>from</c> in <c>died: { value: ..., from: ... }</c>, and
    /// <c>["fields", "rank", "1", "from"]</c> the second entry's. A segment
    /// is an item number only where the file holds a list, so a chapter key
    /// such as <c>14</c> still names a key. Missing keys are added after
    /// their siblings, in the mapping's own style; frontmatter is added if
    /// the file has none. Replacing a scalar keeps the spacing before it and
    /// a comment after it. Replacing a block list or mapping replaces it
    /// whole, including any comments inside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file can't be edited, something on the path isn't a mapping or list, or a list has no such item.</exception>
    /// <exception cref="NotSupportedException">The edit can't be made in place without reformatting, such as filling an empty list item.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    /// <param name="path">The path to set.</param>
    /// <param name="value">The value.</param>
    /// <param name="inTemplateOrder">True to put a new top-level key from the template order (<c>aliases</c>,
    /// <c>status</c>, <c>from</c>, <c>fields</c>, types.md F-TPL-7) in its place in that order, as the write tools
    /// do, rather than after the existing keys.</param>
    public MarkdownFile WithFrontmatterValue(IReadOnlyList<string> path, YamlScalarValue value, bool inTemplateOrder = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        return EditFrontmatter(
            block => YamlEditor.SetScalar(Source!, block, path, value, inTemplateOrder),
            (before, after) => YamlTreeCheck.SameExcept(before, after, path, value, inTemplateOrder),
            addFrontmatter: true,
            $"Setting '{string.Join('.', path)}'");
    }

    /// <summary>
    /// Adds <paramref name="item"/> to the list at <paramref name="path"/>,
    /// such as an alias to <c>["aliases"]</c> or an entry to a temporal list
    /// (format.md §7.3), before item <paramref name="index"/> or at the end.
    /// The other items, their quoting, and comments are kept. The new item
    /// takes the list's style: in a flow list (<c>[a, b]</c>) it goes on the
    /// same line, or on a line of its own if the items have one each; in a
    /// block list it gets its own <c>- item</c> line at the items' indent,
    /// right after the item before it and the comment lines indented under
    /// that item, so comment lines above the next item stay with it; a new
    /// first item goes above the comments over the old first one. It copies
    /// the quoting of the item before it, key by key for a mapping, so a
    /// position next to a plain one stays plain. Where there's nothing to
    /// copy, a string with a <c>:</c> in it, such as a position, is
    /// double-quoted as format.md §7.3 writes them. A missing or null value becomes a new list:
    /// <c>[item]</c> for a scalar, or a block list for a mapping.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is past the end of the list.</exception>
    /// <exception cref="InvalidOperationException">The file can't be edited, or the value at the path isn't a list.</exception>
    /// <exception cref="NotSupportedException">The item can't be added without moving a comment or reformatting.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    /// <param name="path">The list's path.</param>
    /// <param name="item">The new item.</param>
    /// <param name="index">The item it goes before, or null for the end.</param>
    /// <param name="inTemplateOrder">For a new list at a missing top-level key, as for <see cref="WithFrontmatterValue(IReadOnlyList{string}, YamlScalarValue, bool)"/>.</param>
    public MarkdownFile WithNewListItem(IReadOnlyList<string> path, YamlValue item, int? index = null, bool inTemplateOrder = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(item);
        var at = index ?? (GetFrontmatterValue(path) as YamlSequence)?.Items.Count ?? 0;
        return EditFrontmatter(
            block => YamlEditor.AddItem(Source!, block, path, item, index, inTemplateOrder),
            (before, after) => YamlTreeCheck.SameExcept(before, after, path, (b, a) => YamlTreeCheck.AddedItem(b, a, at, item), inTemplateOrder),
            addFrontmatter: true,
            $"Adding an item to '{string.Join('.', path)}'");
    }

    /// <summary>
    /// Sets the value at <paramref name="path"/> (paths as in
    /// <see cref="WithFrontmatterValue(IReadOnlyList{string}, YamlScalarValue)"/>)
    /// to a scalar, a list, or a mapping, such as a field's whole value.
    /// A scalar is set as that overload sets it. A list or mapping is written
    /// in flow style on the key's line (<c>aliases: [Lucy]</c>,
    /// <c>died: { value: x, from: "dracula:7" }</c>), except a list of
    /// mappings under a block key, such as a temporal list, which gets one
    /// <c>- { ... }</c> line per item, as format.md §7.3 writes them. A list
    /// or mapping already at the path is replaced whole, with any comments
    /// inside it. Setting a value that's already there changes nothing
    /// (F-RT-3).
    /// </summary>
    /// <exception cref="InvalidOperationException">The file can't be edited, something on the path isn't a mapping or list, or a list has no such item.</exception>
    /// <exception cref="NotSupportedException">The edit can't be made in place without reformatting.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithFrontmatterValue(IReadOnlyList<string> path, YamlValue value, bool inTemplateOrder = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        if (value is YamlScalarValue scalar)
        {
            return WithFrontmatterValue(path, scalar, inTemplateOrder);
        }

        var current = GetFrontmatterValue(path);
        if (value.MatchesAsWritten(current))
        {
            return this;
        }

        // A collection is replaced in two steps: emptied, then filled.
        var start = current is YamlMapping or YamlSequence ? WithFrontmatterValue(path, YamlScalarValue.Null) : this;
        var what = $"Setting '{string.Join('.', path)}'";
        var result = start.EditFrontmatter(
            block => YamlEditor.SetAny(start.Source!, block, path, value, inTemplateOrder),
            (_, _) => true,
            addFrontmatter: true,
            what);
        if (result.Body != Body || !result.CanEdit
            || !YamlTreeCheck.SameExcept(_frontmatter?.Root, result._frontmatter?.Root, path, (_, edited) => value.MatchesAsWritten(edited), inTemplateOrder))
        {
            throw new EditVerificationException($"{what} didn't read back as written, or changed another value, so the edit was refused.");
        }

        return result;
    }

    /// <summary>
    /// Removes the frontmatter key or list item at <paramref name="path"/>
    /// (paths as in <see cref="WithFrontmatterValue(IReadOnlyList{string}, YamlScalarValue)"/>),
    /// such as <c>["x-color"]</c>, <c>["fields", "died", "from"]</c>, or
    /// <c>["aliases", "0"]</c>. Its lines go, with a comment on its last
    /// line and comment lines right after it indented under it; comment
    /// lines above it stay, unless it was the last key or item of a nested
    /// block mapping or list, whose comment lines inside then go too
    /// (format.md F-RT-6; the overload with <c>emptiedComments</c> says how
    /// many). If it sat between two blank lines, one goes.
    /// Nothing changes if it isn't there: a missing key, an item number past
    /// the end of a list, or a null value on the way. A block list or
    /// mapping that loses its last item is left as <c>key:</c> with no
    /// value, which reads as unset (format.md F-RT-6), unless
    /// <paramref name="emptied"/> is <see cref="EmptiedList.EmptyList"/>, which
    /// leaves a block list under a key as <c>key: []</c>. A flow list or
    /// mapping becomes <c>[]</c> or <c>{}</c> either way. For a chapter's list
    /// fields, where <c>[]</c> means "none here", use <see cref="WithoutChapterFieldItem"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file can't be edited, or the path doesn't fit the file's shape:
    /// a segment that isn't an item number where the file holds a list, or a path into a scalar.</exception>
    /// <exception cref="NotSupportedException">The removal would delete or move a comment that isn't part of it,
    /// or the key is written as an explicit <c>? key</c>.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithoutFrontmatterValue(IReadOnlyList<string> path, EmptiedList emptied = EmptiedList.Null) =>
        WithoutFrontmatterValue(path, emptied, out _);

    /// <summary>
    /// Removes the frontmatter key or list item at <paramref name="path"/>, as
    /// <see cref="WithoutFrontmatterValue(IReadOnlyList{string}, EmptiedList)"/> does.
    /// </summary>
    /// <param name="emptiedComments">How many comment lines went because the removal emptied the block mapping
    /// or list that held it (format.md F-RT-6), beyond the removed key's or item's own; 0 if it emptied nothing.
    /// A write tool reports them, since a comment there may be a commented-out value.</param>
    public MarkdownFile WithoutFrontmatterValue(IReadOnlyList<string> path, EmptiedList emptied, out int emptiedComments)
    {
        ArgumentNullException.ThrowIfNull(path);
        var comments = 0;
        var result = EditFrontmatter(
            block => YamlEditor.Remove(Source!, block, path, emptied, out comments),
            (before, after) => YamlTreeCheck.SameExcept(before, after, path.Take(path.Count - 1).ToList(), (b, a) => YamlTreeCheck.Removed(b, a, path[^1], emptied)),
            addFrontmatter: false,
            $"Removing '{string.Join('.', path)}'");
        emptiedComments = comments;
        return result;
    }

    /// <summary>Removes a field under <c>fields:</c>.</summary>
    public MarkdownFile WithoutField(string field) => WithoutFrontmatterValue(["fields", field]);

    /// <summary>
    /// Removes item <paramref name="item"/> of a list-valued field of the
    /// chapter whose <c>key</c> is <paramref name="chapterKey"/>, in a
    /// <c>book.md</c>'s <c>chapters</c> (format.md F-BOOK-2). If it was the
    /// last item, the field is left as <c>[]</c>, "none here", block or flow
    /// alike, so the chapter doesn't fall back to <c>chapter_defaults</c>
    /// (types.md F-CHAP-5, format.md F-RT-6). Going back to the default is
    /// a separate edit: removing the field.
    /// </summary>
    /// <exception cref="InvalidOperationException">No chapter, or more than one, has that key; or the field isn't a list with that item.</exception>
    /// <exception cref="NotSupportedException">The removal would delete or move a comment that isn't part of it.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithoutChapterFieldItem(string chapterKey, string field, int item) =>
        WithoutChapterFieldItem(chapterKey, field, item, out _);

    /// <inheritdoc cref="WithoutChapterFieldItem(string, string, int)"/>
    /// <param name="emptiedComments">How many comment lines inside the list went with its last item (format.md F-RT-6).</param>
    public MarkdownFile WithoutChapterFieldItem(string chapterKey, string field, int item, out int emptiedComments)
    {
        ArgumentNullException.ThrowIfNull(chapterKey);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(item);
        var chapters = (GetFrontmatterValue("chapters") as YamlSequence)?.Items ?? [];
        var matches = Enumerable.Range(0, chapters.Count)
            .Where(i => (chapters[i] as YamlMapping)?["key"] is YamlScalar { IsNull: false } key && key.Text == chapterKey)
            .ToList();
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"There's no chapter with the key '{chapterKey}'.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException($"{matches.Count} chapters have the key '{chapterKey}', so it doesn't name one chapter.");
        }

        string[] listPath = ["chapters", matches[0].ToString(System.Globalization.CultureInfo.InvariantCulture), "fields", field];
        return WithoutListItemAsEmpty(listPath, item, $"'{field}' in chapter '{chapterKey}'", out emptiedComments);
    }

    /// <summary>
    /// Removes item <paramref name="item"/> of a list-valued field in this
    /// container's <c>chapter_defaults</c> (<c>universe.md</c>,
    /// <c>series.md</c>, or <c>book.md</c>). If it was the last item, the
    /// field is left as <c>[]</c>, "none here", block or flow alike, so
    /// inheritance from the container above stops (types.md F-CHAP-5,
    /// format.md F-RT-6). Going back to inheriting is a separate edit:
    /// removing the field.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field isn't a list in <c>chapter_defaults</c>, or has no such item.</exception>
    /// <exception cref="NotSupportedException">The removal would delete or move a comment that isn't part of it.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithoutChapterDefaultItem(string field, int item) =>
        WithoutChapterDefaultItem(field, item, out _);

    /// <inheritdoc cref="WithoutChapterDefaultItem(string, int)"/>
    /// <param name="emptiedComments">How many comment lines inside the list went with its last item (format.md F-RT-6).</param>
    public MarkdownFile WithoutChapterDefaultItem(string field, int item, out int emptiedComments)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(item);
        return WithoutListItemAsEmpty(["chapter_defaults", field], item, $"'{field}' in chapter_defaults", out emptiedComments);
    }

    /// <summary>Removes one item of the list at <paramref name="listPath"/>, leaving <c>[]</c> if it was the last.</summary>
    private MarkdownFile WithoutListItemAsEmpty(string[] listPath, int item, string what, out int emptiedComments)
    {
        if (GetFrontmatterValue(listPath) is not YamlSequence list)
        {
            throw new InvalidOperationException($"{what} isn't a list.");
        }

        if (item >= list.Items.Count)
        {
            throw new InvalidOperationException($"{what} has no item {item}.");
        }

        return WithoutFrontmatterValue([.. listPath, item.ToString(System.Globalization.CultureInfo.InvariantCulture)], EmptiedList.EmptyList, out emptiedComments);
    }

    /// <summary>Applies a frontmatter edit and checks that it read back as intended.</summary>
    /// <param name="edit">The edits for the frontmatter block.</param>
    /// <param name="check">Judges the old and new frontmatter trees.</param>
    /// <param name="addFrontmatter">True if the edit adds frontmatter to a
    /// file with none; otherwise there's nothing to change.</param>
    /// <param name="what">The edit, for the error message.</param>
    private MarkdownFile EditFrontmatter(Func<YamlBlock, List<TextEdit>> edit, Func<YamlNode?, YamlNode?, bool> check, bool addFrontmatter, string what)
    {
        // Adding frontmatter to a file that has none fixes F-FM-1, so that
        // error doesn't block this edit.
        EnsureEditable(_frontmatter is null ? _missingFrontmatter : null);

        List<TextEdit> edits;
        if (_frontmatter is null)
        {
            if (!addFrontmatter)
            {
                return this;
            }

            // New frontmatter goes at the very start, after any BOM.
            var nl = Source!.Newline;
            var empty = YamlBlock.Parse(Source, 0, 0);
            var entry = edit(empty).Single().NewText;
            edits = [new TextEdit(0, 0, "---" + nl + entry + "---" + nl)];
        }
        else
        {
            edits = edit(_frontmatter);
        }

        if (edits.Count == 0)
        {
            return this;
        }

        var result = Reparse(edits);
        if (result.Body != Body || !result.CanEdit || !check(_frontmatter?.Root, result._frontmatter?.Root))
        {
            throw new EditVerificationException($"{what} didn't read back as written, or changed another value, so the edit was refused.");
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
        CheckOwnSection(section);

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
        CheckSectionName(name);

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

    /// <summary>
    /// Adds a section just before <paramref name="before"/>, or at the end of
    /// the file if it's null: a heading of <paramref name="level"/>, then a
    /// section comment (<c>&lt;!-- wolfden: <paramref name="comment"/> --&gt;</c>,
    /// format.md §9.2) if one is given, then its text. A blank line separates
    /// it from the text before it and from the heading after it. Where it
    /// lands decides what holds it: a level-3 section added before a level-2
    /// one becomes a subsection of the section before that.
    /// </summary>
    /// <param name="name">The heading text.</param>
    /// <param name="body">The section's text.</param>
    /// <param name="level">2 for a section, 3 to 6 for a subsection.</param>
    /// <param name="before">The section the new one goes in front of, or null for the end of the file.</param>
    /// <param name="comment">The section comment's attributes, such as <c>status=idea from=dracula:5</c>, or null for none.</param>
    /// <exception cref="ArgumentException">The name, comment, or text wouldn't read back as one new section in that
    /// place with nothing else changed, or <paramref name="before"/> belongs to a different file.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithNewSection(string name, string body, int level, Section? before, string? comment = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 6);
        EnsureEditable();
        CheckSectionName(name);
        if (before is not null)
        {
            CheckOwnSection(before, nameof(before));
        }

        string? commentLine = null;
        if (comment is not null)
        {
            var attributes = comment.Trim();
            if (attributes.Length == 0 || attributes.Contains('\n') || attributes.Contains('\r') || attributes.Contains("-->", StringComparison.Ordinal))
            {
                throw new ArgumentException("A section comment's attributes are one line of key=value pairs.", nameof(comment));
            }

            commentLine = "<!-- wolfden: " + attributes + " -->";
        }

        var source = Source!;
        var nl = source.Newline;
        var text = source.Text;
        var bodyLines = SplitLines(body);
        var lines = new List<string> { new string('#', level) + " " + name };
        if (commentLine is not null)
        {
            lines.Add(commentLine);
        }

        lines.AddRange(bodyLines);
        var block = string.Join(nl, lines);

        TextEdit edit;
        int index;
        if (before is null)
        {
            index = Sections.Count;
            if (text.Length == 0)
            {
                edit = new TextEdit(0, 0, block + nl);
            }
            else if (!source.EndsWithNewline)
            {
                edit = new TextEdit(text.Length, 0, nl + nl + block);
            }
            else
            {
                var lastLine = source.LineText(source.Lines.Count - 2);
                edit = new TextEdit(text.Length, 0, (IsBlank(lastLine) ? "" : nl) + block + nl);
            }
        }
        else
        {
            index = before.Index;
            var line = source.LineIndexOf(before.HeadingStart);
            var blankBefore = line == 0 || IsBlank(source.LineText(line - 1)) || source.Lines[line - 1].EndIncludingTerminator <= _bodyStart;
            edit = new TextEdit(before.HeadingStart, 0, (blankBefore ? "" : nl) + block + nl + nl);
        }

        var result = Reparse([edit]);
        if (result.FrontmatterText != FrontmatterText)
        {
            throw new EditVerificationException("Adding a section changed the frontmatter, so the edit was refused.");
        }

        if (TrimBlankLines(result.Lead) != TrimBlankLines(Lead) || result.Title != Title)
        {
            throw new ArgumentException($"'{name}' would read as part of the text before the sections, not as a new section.", nameof(name));
        }

        var expected = Shapes().ToList();
        expected.Insert(index, new SectionShape(name, level, commentLine, string.Join(nl, bodyLines)));
        if (!expected.SequenceEqual(result.Shapes()))
        {
            throw new ArgumentException($"'{name}', its comment, and its text wouldn't read back as one new section there with nothing else changed.", nameof(name));
        }

        return result;
    }

    /// <summary>
    /// Adds text at the end of the lead (format.md F-SEC-2), after what's
    /// there and before the first section, with a blank line before it and
    /// one before the heading after it. The title and every section are
    /// kept as written.
    /// </summary>
    /// <exception cref="ArgumentException">The text would add or change a heading or section comment.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithTextAddedToLead(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureEditable();
        var lines = SplitLines(text);
        if (lines.Length == 0)
        {
            return this;
        }

        var source = Source!;
        var nl = source.Newline;
        var all = source.Text;
        var block = string.Join(nl, lines);
        TextEdit edit;
        if (_leadEnd >= all.Length)
        {
            if (all.Length == 0 || all.Length == _bodyStart)
            {
                edit = new TextEdit(all.Length, 0, block + nl);
            }
            else if (!source.EndsWithNewline)
            {
                edit = new TextEdit(all.Length, 0, nl + nl + block);
            }
            else
            {
                var lastLine = source.LineText(source.Lines.Count - 2);
                edit = new TextEdit(all.Length, 0, (IsBlank(lastLine) ? "" : nl) + block + nl);
            }
        }
        else
        {
            var line = source.LineIndexOf(_leadEnd);
            var blankBefore = _leadEnd == _bodyStart || line == 0 || IsBlank(source.LineText(line - 1));
            edit = new TextEdit(_leadEnd, 0, (blankBefore ? "" : nl) + block + nl + nl);
        }

        var result = Reparse([edit]);
        if (result.FrontmatterText != FrontmatterText)
        {
            throw new EditVerificationException("Adding to the lead changed the frontmatter, so the edit was refused.");
        }

        if (result.Title != Title || !Shapes().SequenceEqual(result.Shapes()))
        {
            throw new ArgumentException("The text would add or change a heading or section comment, so it can't go in the lead.", nameof(text));
        }

        if (!TrimBlankLines(result.Lead).EndsWith(block, StringComparison.Ordinal))
        {
            throw new EditVerificationException("The text added to the lead didn't read back as written, so the edit was refused.");
        }

        return result;
    }

    /// <summary>
    /// Replaces the lead's text (format.md F-SEC-2): everything between the
    /// frontmatter and the first section, title heading included, as
    /// <see cref="WithSectionBody"/> replaces a section's. The blank lines
    /// around the old text are kept, and blank lines at the start and end of
    /// <paramref name="text"/> are dropped. Line breaks in
    /// <paramref name="text"/> are converted to the file's own. The
    /// frontmatter and every section are kept as written; the title may
    /// change, since it's part of the lead.
    /// </summary>
    /// <exception cref="ArgumentException">The text would add or change a heading or section comment.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithLead(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureEditable();

        var source = Source!;
        var nl = source.Newline;
        var newLines = SplitLines(text);
        int first = -1, last = -1;
        if (_leadEnd > _bodyStart)
        {
            for (var i = source.LineIndexOf(_bodyStart); i < source.Lines.Count && source.Lines[i].Start < _leadEnd; i++)
            {
                if (!IsBlank(source.LineText(i)))
                {
                    first = first < 0 ? i : first;
                    last = i;
                }
            }
        }

        if (first < 0)
        {
            // No text yet: replacing it is adding it.
            return WithTextAddedToLead(text);
        }

        var contentStart = source.Lines[first].Start;
        var lastLine = source.Lines[last];
        var hasTerminator = lastLine.TerminatorLength > 0;
        TextEdit edit;
        if (newLines.Length > 0)
        {
            edit = new TextEdit(contentStart, lastLine.EndIncludingTerminator - contentStart, string.Join(nl, newLines) + (hasTerminator ? nl : ""));
        }
        else if (hasTerminator)
        {
            edit = new TextEdit(contentStart, lastLine.EndIncludingTerminator - contentStart, "");
        }
        else
        {
            // Clearing a lead at the end of a file with no final newline: drop
            // the line break before it too, unless that's the frontmatter's.
            var from = first > 0 && source.Lines[first - 1].Start >= _bodyStart ? source.Lines[first - 1].End : contentStart;
            edit = new TextEdit(from, lastLine.End - from, "");
        }

        var result = Reparse([edit]);
        if (result.FrontmatterText != FrontmatterText)
        {
            throw new EditVerificationException("Replacing the lead changed the frontmatter, so the edit was refused.");
        }

        if (!Shapes().SequenceEqual(result.Shapes()))
        {
            throw new ArgumentException(
                "The new text would add or change a heading or section comment, so it can't go in the lead. Edit one section at a time.", nameof(text));
        }

        if (TrimBlankLines(result.Lead) != string.Join(nl, newLines))
        {
            throw new EditVerificationException("The lead didn't read back as written, so the edit was refused.");
        }

        return result;
    }

    /// <summary>
    /// Replaces ranges of the body's text, such as the heading part of links
    /// rewritten after a section rename (format.md F-RT-5). Each range must
    /// lie in the body. The frontmatter, the title, and every section's
    /// level and section comment must read the same afterwards.
    /// </summary>
    /// <exception cref="ArgumentException">A range isn't in the body, or the result would change the file's outline.</exception>
    internal MarkdownFile WithBodyTextReplaced(IReadOnlyList<(int Start, int Length, string Text)> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        EnsureEditable();
        if (replacements.Count == 0)
        {
            return this;
        }

        var all = Source!.Text;
        if (replacements.Any(r => r.Start < _bodyStart || r.Length < 0 || r.Start + r.Length > all.Length))
        {
            throw new ArgumentException("A replacement lies outside the body.", nameof(replacements));
        }

        var result = Reparse(replacements.Select(r => new TextEdit(r.Start, r.Length, r.Text)));
        if (result.FrontmatterText != FrontmatterText || result.Title != Title || result.Sections.Count != Sections.Count
            || result.Sections.Zip(Sections).Any(p => p.First.HeadingLevel != p.Second.HeadingLevel || p.First.Comment?.Text != p.Second.Comment?.Text))
        {
            throw new ArgumentException("The replacements would change the file's headings or section comments.", nameof(replacements));
        }

        return result;
    }

    /// <summary>
    /// Replaces ranges of the frontmatter's text that lie inside string
    /// values, such as a link written verbatim in a field, splicing the new
    /// text in so the value keeps its style (a block scalar stays a block
    /// scalar). <paramref name="expected"/> gives what each changed value
    /// must then read as; every other value must read the same.
    /// </summary>
    /// <exception cref="ArgumentException">A range isn't in the frontmatter, or the values wouldn't read back as expected.</exception>
    internal MarkdownFile WithFrontmatterTextReplaced(
        IReadOnlyList<(int Start, int Length, string Text)> replacements, IReadOnlyList<(IReadOnlyList<string> Path, string Value)> expected)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(expected);
        EnsureEditable();
        if (replacements.Count == 0)
        {
            return this;
        }

        if (_frontmatter is null || replacements.Any(r => r.Start < 0 || r.Length < 0 || r.Start + r.Length > _frontmatterEnd))
        {
            throw new ArgumentException("A replacement lies outside the frontmatter.", nameof(replacements));
        }

        var result = Reparse(replacements.Select(r => new TextEdit(r.Start, r.Length, r.Text)));
        var changed = expected.ToDictionary(e => string.Join('/', e.Path), e => e.Value, StringComparer.Ordinal);
        if (result.Body != Body || !result.CanEdit || !YamlTreeCheck.SameExceptScalars(_frontmatter.Root, result._frontmatter?.Root, changed))
        {
            throw new ArgumentException("The frontmatter wouldn't read back with only those values changed.", nameof(replacements));
        }

        return result;
    }

    /// <summary>
    /// Changes the display title, the first level-1 heading in the lead
    /// (F-SEC-2): only the heading's text changes, as
    /// <see cref="WithSectionName"/> changes a section's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has no title.</exception>
    /// <exception cref="ArgumentException">The title isn't one line of text, or wouldn't read back as the title with nothing else changed.</exception>
    public MarkdownFile WithTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        EnsureEditable();
        CheckSectionName(title);
        if (_titleSpan is not { } span || Title is null)
        {
            throw new InvalidOperationException("The file has no level-1 heading in its lead, so it has no title to change.");
        }

        if (Title == title)
        {
            return this;
        }

        var text = Source!.Text;
        var insert = span.Start == span.End && span.Start > 0 && text[span.Start - 1] == '#' ? " " + title : title;
        var result = Reparse([new TextEdit(span.Start, span.End - span.Start, insert)]);
        if (result.FrontmatterText != FrontmatterText || result.Title != title || !Shapes().SequenceEqual(result.Shapes()))
        {
            throw new ArgumentException($"'{title}' wouldn't read back as the file's title with nothing else changed.", nameof(title));
        }

        return result;
    }

    /// <summary>
    /// Renames a section: only the heading's text changes. Its <c>#</c>
    /// markers, any closing <c>#</c>s or trailing HTML comment, a setext
    /// underline, its section comment, and its text are kept. A setext
    /// heading whose name runs over several lines gets the new name on one.
    /// This edits one file. Format.md F-RT-5 also requires links to the old
    /// name (<c>[[entity#Old name]]</c>) to be rewritten when the app or an
    /// MCP tool renames a section; that den-wide rewrite belongs to the write
    /// tools (#21).
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't one line of text, or wouldn't read back as the section's
    /// name with nothing else changed (for example, a setext heading renamed to <c>- x</c> becomes a list item).</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithSectionName(Section section, string name)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(name);
        EnsureEditable();
        CheckOwnSection(section);
        CheckSectionName(name);
        if (section.Name == name)
        {
            return this;
        }

        var text = Source!.Text;
        var insert = section.NameStart == section.NameEnd && section.NameStart > 0 && text[section.NameStart - 1] == '#' ? " " + name : name;
        var result = Reparse([new TextEdit(section.NameStart, section.NameEnd - section.NameStart, insert)]);
        if (result.FrontmatterText != FrontmatterText)
        {
            throw new EditVerificationException("Renaming a section changed the frontmatter, so the edit was refused.");
        }

        if (result.Lead != Lead || result.Title != Title)
        {
            // For example a setext heading renamed to `- x` becomes a list item.
            throw new ArgumentException($"'{name}' wouldn't read back as a heading: the text before the sections would change.", nameof(name));
        }

        var expected = Shapes().ToList();
        expected[section.Index] = expected[section.Index] with { Name = name };
        if (!expected.SequenceEqual(result.Shapes()))
        {
            throw new ArgumentException($"'{name}' wouldn't read back as the name of '{section.Name}' with nothing else changed.", nameof(name));
        }

        return result;
    }

    /// <summary>
    /// Removes a section with its heading, section comment, text, and
    /// subsections, up to the next heading of the same or a higher level.
    /// The blank lines before the next heading stay, so the spacing between
    /// the sections around it doesn't change. Removing the last section also
    /// removes the blank lines before it, and the file ends as it did, with
    /// or without a final newline (F-ENC-4).
    /// </summary>
    /// <exception cref="ArgumentException">The section belongs to a different file.</exception>
    /// <exception cref="NotSupportedException">Without the section, the text around it would read differently,
    /// for example a paragraph running into a setext heading; or the section holds a link reference
    /// definition (<c>[ref]: https://…</c>), which links elsewhere in the file may use.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithoutSection(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        EnsureEditable();
        CheckOwnSection(section);

        var source = Source!;
        var start = section.HeadingStart;
        if (section.End == source.Text.Length)
        {
            var line = source.LineIndexOf(start) - 1;
            while (line >= 0 && IsBlank(source.LineText(line)))
            {
                line--;
            }

            // The frontmatter's closing line keeps its line break.
            start = line < 0 ? 0
                : source.EndsWithNewline || source.Lines[line].EndIncludingTerminator <= _bodyStart ? source.Lines[line].EndIncludingTerminator
                : source.Lines[line].End;
        }

        // A link reference definition (`[ref]: https://…`) serves the whole
        // file, so removing one could turn links in other sections into plain
        // text. Refused rather than checked link by link.
        if (BodyParser.HasLinkReferenceDefinition(source.Text[section.HeadingStart..section.End]))
        {
            throw new NotSupportedException($"'{section.Name}' holds a link reference definition, which links elsewhere in the file may use, so it can't be removed in place. Move the definition out first.");
        }

        var result = Reparse([new TextEdit(start, section.End - start, "")]);
        if (result.FrontmatterText != FrontmatterText)
        {
            throw new EditVerificationException("Removing a section changed the frontmatter, so the edit was refused.");
        }

        if (TrimBlankLines(result.Lead) != TrimBlankLines(Lead) || result.Title != Title)
        {
            // For example a stray level-1 heading after it would become the title.
            throw new NotSupportedException($"Without '{section.Name}', the text before the sections would change, so it can't be removed in place.");
        }

        var removed = Sections.Count(s => s.Index >= section.Index && s.HeadingStart < section.End);
        var expected = Shapes().ToList();
        expected.RemoveRange(section.Index, removed);
        if (!expected.SequenceEqual(result.Shapes()))
        {
            throw new NotSupportedException($"Without '{section.Name}', the text around it would read differently, so it can't be removed in place.");
        }

        return result;
    }

    /// <summary>
    /// Moves a section, with its subsections, to just before
    /// <paramref name="before"/>, or after the last of its siblings if
    /// <paramref name="before"/> is null. Sections move only among their
    /// siblings: the level-2 sections, or the subsections of one section.
    /// Each section's text moves as written. The blank lines between
    /// sections stay where they are, so the file keeps its spacing and ends
    /// as it did (F-ENC-4).
    /// </summary>
    /// <exception cref="ArgumentException">A section belongs to a different file, or <paramref name="before"/> isn't a sibling.</exception>
    /// <exception cref="NotSupportedException">In its new place, the text would read differently,
    /// for example a paragraph running into a setext heading.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public MarkdownFile WithSectionMovedBefore(Section section, Section? before)
    {
        ArgumentNullException.ThrowIfNull(section);
        EnsureEditable();
        CheckOwnSection(section);
        if (before is not null)
        {
            CheckOwnSection(before, nameof(before));
            if (!ReferenceEquals(before.Parent, section.Parent))
            {
                throw new ArgumentException("A section moves only among its siblings, the sections with the same parent.", nameof(before));
            }
        }

        if (ReferenceEquals(section, before))
        {
            return this;
        }

        var siblings = (section.Parent?.Subsections ?? TopLevelSections).ToList();
        var order = siblings.Where(s => !ReferenceEquals(s, section)).ToList();
        order.Insert(before is null ? order.Count : order.IndexOf(before), section);
        if (order.SequenceEqual(siblings))
        {
            return this;
        }

        // Each sibling is its text (up to its last non-blank line) and the
        // line breaks after it. The texts trade places; the breaks don't.
        var source = Source!;
        var text = source.Text;

        // The first of two same-labelled link reference definitions wins, so
        // reordering sections that hold both could change where links go.
        if (BodyParser.HasDuplicateLinkReferenceLabels(text[siblings[0].HeadingStart..siblings[^1].End]))
        {
            throw new NotSupportedException($"Two of the sections around '{section.Name}' define the same link reference label, so moving it could change where links go. Remove one definition first.");
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < siblings.Count; i++)
        {
            sb.Append(text, order[i].HeadingStart, TextEnd(order[i]) - order[i].HeadingStart)
                .Append(text, TextEnd(siblings[i]), siblings[i].End - TextEnd(siblings[i]));
        }

        var regionStart = siblings[0].HeadingStart;
        var result = Reparse([new TextEdit(regionStart, siblings[^1].End - regionStart, sb.ToString())]);
        if (result.FrontmatterText != FrontmatterText)
        {
            throw new EditVerificationException("Moving a section changed the frontmatter, so the edit was refused.");
        }

        if (result.Lead != Lead || result.Title != Title)
        {
            // For example a setext heading moved first would run into the lead's last paragraph.
            throw new NotSupportedException($"In its new place, '{section.Name}' would change the text before the sections, so it can't be moved in place.");
        }

        var shapes = Shapes().ToList();
        var expected = shapes.Take(siblings[0].Index).ToList();
        foreach (var moved in order)
        {
            expected.AddRange(shapes.Where((_, i) => i >= moved.Index && Sections[i].HeadingStart < moved.End));
        }

        expected.AddRange(shapes.Skip(expected.Count));
        if (!expected.SequenceEqual(result.Shapes()))
        {
            throw new NotSupportedException($"In its new place, '{section.Name}' or the text around it would read differently, so it can't be moved in place.");
        }

        return result;

        // The end of a section's last non-blank line, before its line break.
        int TextEnd(Section s)
        {
            var line = source.LineIndexOf(Math.Max(s.HeadingStart, s.End - 1));
            while (line > 0 && source.Lines[line].Start > s.HeadingStart && IsBlank(source.LineText(line)))
            {
                line--;
            }

            return Math.Min(source.Lines[line].End, s.End);
        }
    }

    /// <summary>What a section reads as: the parts an edit to another section must not change.</summary>
    private readonly record struct SectionShape(string Name, int HeadingLevel, string? Comment, string Body);

    private IEnumerable<SectionShape> Shapes() =>
        Sections.Select(s => new SectionShape(s.Name, s.HeadingLevel, s.Comment?.Text, TrimBlankLines(s.Body)));

    private void CheckOwnSection(Section section, string paramName = "section")
    {
        if (!ReferenceEquals(section.Owner, this))
        {
            throw new ArgumentException("The section belongs to a different file.", paramName);
        }
    }

    private static void CheckSectionName(string name)
    {
        if (name.Length == 0 || name != name.Trim() || name.Contains('\n') || name.Contains('\r'))
        {
            throw new ArgumentException("A section name is one line of text, with no leading or trailing spaces.", nameof(name));
        }
    }

    private MarkdownFile Reparse(IEnumerable<TextEdit> edits) => new(ApplyEdits(edits), _requireFrontmatter, _isOutline);

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
