using Wolfden.Core.Text;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Documents;

/// <summary>
/// A den file parsed for reading and minimal editing. Files are immutable:
/// each edit returns a new file. A file that nothing edited writes back
/// exactly the bytes it was parsed from (format.md F-RT-3), and an edit
/// changes only the bytes of what it touches (F-RT-2).
/// </summary>
public abstract class DenFile
{
    /// <summary>Rules whose errors leave a file too broken to edit safely.</summary>
    private static readonly HashSet<string> StructuralRules = new(StringComparer.Ordinal)
    {
        "F-ENC-1", "F-ENC-3", "F-FM-1", "F-YAML-1", "F-YAML-2",
    };

    private readonly byte[] _bytes;
    private readonly List<Diagnostic> _diagnostics = [];

    private protected DenFile(byte[] bytes)
    {
        _bytes = bytes;
        Source = SourceText.TryDecode(bytes, out var error);
        if (error is not null)
        {
            _diagnostics.Add(error);
        }
        else
        {
            _diagnostics.AddRange(Source!.Diagnose());
        }
    }

    /// <summary>False when the bytes aren't valid UTF-8 (F-ENC-1). Such a
    /// file has no text, sections, or frontmatter, and can't be edited.</summary>
    public bool IsReadable => Source is not null;

    /// <summary>The file's text, without any byte order mark.</summary>
    public string Text => Source?.Text ?? throw new InvalidOperationException("The file isn't valid UTF-8.");

    /// <summary>True if the file starts with a UTF-8 byte order mark (F-ENC-2).</summary>
    public bool HasByteOrderMark => Source?.HasBom ?? false;

    /// <summary>The file's line endings (F-ENC-3).</summary>
    public LineEndingKind LineEndings => Source?.LineEndings ?? LineEndingKind.None;

    /// <summary>True if the file ends with a line ending (F-ENC-4).</summary>
    public bool EndsWithNewline => Source?.EndsWithNewline ?? false;

    /// <summary>Errors and warnings found while parsing, in file order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>True if any diagnostic is an error.</summary>
    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// True if the file parsed well enough to edit: valid UTF-8, no lone CR,
    /// and frontmatter or YAML that parses to a mapping. Other errors don't
    /// block an edit here; whether a write may go ahead is the caller's
    /// decision (format.md §1.3).
    /// </summary>
    public bool CanEdit => !_diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && StructuralRules.Contains(d.RuleId));

    private protected SourceText? Source { get; }

    /// <summary>The file's bytes. Unedited, they're the bytes it was parsed from.</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    internal string Slice(int start, int end) => Text[start..end];

    private protected void AddDiagnostics(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    private protected void SortDiagnostics()
    {
        var sorted = _diagnostics.OrderBy(d => d.Line ?? 0).ToList();
        _diagnostics.Clear();
        _diagnostics.AddRange(sorted);
    }

    private protected void EnsureEditable()
    {
        if (!CanEdit)
        {
            var reasons = string.Join("; ", _diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && StructuralRules.Contains(d.RuleId)));
            throw new InvalidOperationException($"The file can't be edited until these errors are fixed: {reasons}");
        }
    }

    /// <summary>Applies text edits and encodes the result, keeping the byte order mark.</summary>
    private protected byte[] ApplyEdits(IEnumerable<TextEdit> edits)
    {
        var text = TextEdit.Apply(Source!.Text, edits);
        return SourceText.FromText(text, Source.HasBom).Encode();
    }

    /// <summary>Finds the node at <paramref name="path"/> under <paramref name="root"/>.</summary>
    private protected static YamlNode? Find(YamlNode? root, IReadOnlyList<string> path)
    {
        var node = root;
        foreach (var key in path)
        {
            if (node is not YamlMapping mapping)
            {
                return null;
            }

            node = mapping[key];
        }

        return node;
    }
}
