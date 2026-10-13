namespace Wolfden.Core.Documents;

/// <summary>One <c>key=value</c> pair in a section comment (format.md F-SEC-7).</summary>
/// <param name="Name">The attribute name, such as <c>from</c>.</param>
/// <param name="Value">The value as written, without its quotes.</param>
/// <param name="IsQuoted">True if the value was double-quoted.</param>
public sealed record SectionAttribute(string Name, string Value, bool IsQuoted);

/// <summary>
/// A section comment such as <c>&lt;!-- wolfden: from=ember:19 --&gt;</c>
/// (format.md §9.2), parsed into its attributes. Values are kept as raw
/// text: positions are parsed elsewhere (#11).
/// </summary>
public sealed class SectionComment
{
    internal SectionComment(int line, string text, IReadOnlyList<SectionAttribute> attributes)
    {
        Line = line;
        Text = text;
        Attributes = attributes;
    }

    /// <summary>The 1-based line of the comment in the file.</summary>
    public int Line { get; }

    /// <summary>The comment line as written, without its line ending.</summary>
    public string Text { get; }

    /// <summary>The attributes, in the order written, including unknown and repeated ones.</summary>
    public IReadOnlyList<SectionAttribute> Attributes { get; }

    /// <summary>The value of the first attribute named <paramref name="name"/>, or null.</summary>
    public string? this[string name] => Attributes.FirstOrDefault(a => a.Name == name)?.Value;
}

/// <summary>
/// A section: a heading of level 2 to 6 and what follows it (format.md §9.1).
/// Positions inside it refer to the <see cref="MarkdownFile"/> it came from.
/// </summary>
public sealed class Section
{
    private readonly List<Section> _subsections = [];

    internal Section(MarkdownFile owner, int index)
    {
        Owner = owner;
        Index = index;
    }

    /// <summary>The file this section belongs to.</summary>
    public MarkdownFile Owner { get; }

    /// <summary>The section's position among all sections of the file, in file order.</summary>
    public int Index { get; }

    /// <summary>
    /// The heading text without its markers, closing <c>#</c>s, or trailing
    /// HTML comments, trimmed, with inline markup kept as written
    /// (format.md F-SEC-3). The lines of a multi-line setext heading are
    /// joined with single spaces.
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>The section's level: 2 for an entity section, 3 to 6 for a
    /// subsection. A level-1 heading after the first level-2 one counts as 2
    /// (format.md F-SEC-2).</summary>
    public int Level { get; internal set; }

    /// <summary>The level as written (1 to 6).</summary>
    public int HeadingLevel { get; internal set; }

    /// <summary>True for a setext heading (underlined with <c>---</c>).</summary>
    public bool IsSetext { get; internal set; }

    /// <summary>The 1-based line where the heading starts.</summary>
    public int Line { get; internal set; }

    /// <summary>The 1-based line where <see cref="Body"/> starts: the line
    /// after the heading, or after its section comment. For an empty body at
    /// the end of the file, it's the line after the last one.</summary>
    public int BodyLine { get; internal set; }

    /// <summary>The section comment under the heading, or null if there isn't
    /// a valid one (format.md F-SEC-6).</summary>
    public SectionComment? Comment { get; internal set; }

    /// <summary>The section that holds this one, or null for a level-2 section.</summary>
    public Section? Parent { get; internal set; }

    /// <summary>The subsections directly under this one, in file order.</summary>
    public IReadOnlyList<Section> Subsections => _subsections;

    /// <summary>The heading's source text, including its line endings
    /// (and a setext underline).</summary>
    public string HeadingText => Owner.Slice(HeadingStart, HeadingEnd);

    /// <summary>
    /// The section's own text, as written: everything after the heading and
    /// its section comment up to the next heading of any level. Subsections
    /// aren't included.
    /// </summary>
    public string Body => Owner.Slice(BodyStart, OwnEnd);

    /// <summary>The whole section as written, from its heading to the next
    /// heading of the same or a higher level, subsections included.</summary>
    public string Text => Owner.Slice(HeadingStart, End);

    internal int HeadingStart { get; set; }

    internal int HeadingEnd { get; set; }

    internal int BodyStart { get; set; }

    internal int OwnEnd { get; set; }

    internal int End { get; set; }

    internal void AddSubsection(Section section) => _subsections.Add(section);

    /// <inheritdoc />
    public override string ToString() => $"{new string('#', Level)} {Name}";
}
