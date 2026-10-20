using Wolfden.Core.Documents;
using Wolfden.Core.Outlines;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Checks;

/// <summary>Where a row id is written: one row of one outline.</summary>
/// <param name="Path">The outline, relative to the den root.</param>
/// <param name="Line">The row's line.</param>
internal sealed record RowPlace(string Path, int Line);

/// <summary>
/// The rows of a den's outlines, grouped as <c>row</c> fields resolve them
/// (format.md F-FLD-7, F-OUT-5): by series, or by standalone book.
/// </summary>
internal sealed class RowIndex
{
    private readonly Dictionary<Container, Dictionary<string, List<RowPlace>>> _groups = [];

    /// <summary>The rows of a series' outlines, or of a standalone book's.</summary>
    public IReadOnlyDictionary<string, List<RowPlace>> Group(Container group) =>
        _groups.TryGetValue(group, out var rows) ? rows : new Dictionary<string, List<RowPlace>>();

    /// <summary>The groups a <c>row</c> field resolves among, from an entity's scope (F-FLD-7).</summary>
    public static IEnumerable<Container> GroupsFor(Container scope) => scope switch
    {
        Book { Series: { } series } => [series],
        Book book => [book],
        Series series => [series],
        Universe universe => universe.Works,
        _ => [],
    };

    /// <summary>The group a book's rows belong to: its series, or the book itself if it's standalone.</summary>
    public static Container GroupOf(Book book) => (Container?)book.Series ?? book;

    public void Add(Container group, string rowId, RowPlace place)
    {
        if (!_groups.TryGetValue(group, out var rows))
        {
            _groups[group] = rows = new Dictionary<string, List<RowPlace>>(StringComparer.Ordinal);
        }

        if (!rows.TryGetValue(rowId, out var places))
        {
            rows[rowId] = places = [];
        }

        places.Add(place);
    }
}

/// <summary>
/// The outline checks of <c>wolfden check</c> (format.md §11): each book's
/// <c>outline.md</c> parse diagnostics, the warnings that depend only on the
/// file (<see cref="OutlineReader.Read"/>), and the ones that span files:
/// chapters that don't exist (F-OUT-9), rows out of chapter order (F-OUT-10),
/// repeated row ids (F-OUT-5), and row ids that don't keep counting across a
/// series (F-OUT-6).
/// </summary>
internal static class OutlineChecks
{
    public static List<DenDiagnostic> Run(DenStructure structure, IDenSource source, out RowIndex index)
    {
        var diagnostics = new List<DenDiagnostic>();
        index = new RowIndex();
        foreach (var universe in structure.Universes)
        {
            var read = new Dictionary<Book, (string Path, IReadOnlyList<OutlineRow> Rows)>();
            foreach (var book in universe.Books)
            {
                var path = $"{book.Path}/outline.md";
                if (!source.FileExists(path))
                {
                    continue;
                }

                var file = MarkdownFile.ParseOutline(source.ReadFile(path));
                diagnostics.AddRange(file.Diagnostics.Select(d => new DenDiagnostic(path, d)));
                if (OutlineReader.IsExcluded(file))
                {
                    continue;
                }

                var outline = OutlineReader.Read(file);
                diagnostics.AddRange(outline.Diagnostics.Select(d => new DenDiagnostic(path, d)));
                diagnostics.AddRange(CheckChapters(book, outline.Rows).Select(d => new DenDiagnostic(path, d)));
                read[book] = (path, outline.Rows);
                foreach (var row in outline.Rows)
                {
                    index.Add(RowIndex.GroupOf(book), row.RowId, new RowPlace(path, row.Line));
                }
            }

            foreach (var group in universe.Works)
            {
                diagnostics.AddRange(Repeats(group, index.Group(group)));
            }

            foreach (var series in universe.Series)
            {
                diagnostics.AddRange(Counting(series, read));
            }
        }

        return diagnostics;
    }

    /// <summary>F-OUT-9 (a chapter that isn't in the book) and F-OUT-10 (a row mapped to an earlier chapter than a row above it).</summary>
    private static IEnumerable<Diagnostic> CheckChapters(Book book, IReadOnlyList<OutlineRow> rows)
    {
        (OutlineRow Row, Chapter Chapter)? latest = null;
        foreach (var row in rows)
        {
            if (row.Chapter is not { } key)
            {
                continue;
            }

            if (book.FindChapter(key) is not { } chapter)
            {
                yield return new Diagnostic("F-OUT-9", DiagnosticSeverity.Warning, row.Line,
                    $"Row {row.RowId}: '{key}' isn't a chapter of book '{book.Key}', so the row isn't mapped to a chapter.");
                continue;
            }

            if (latest is { } above && chapter.Index < above.Chapter.Index)
            {
                yield return new Diagnostic("F-OUT-10", DiagnosticSeverity.Warning, row.Line,
                    $"Row {row.RowId} is mapped to chapter '{chapter.Key}', which comes before chapter '{above.Chapter.Key}' of row {above.Row.RowId} (line {above.Row.Line}) above it. Rows should follow chapter order.");
            }

            if (latest is null || chapter.Index > latest.Value.Chapter.Index)
            {
                latest = (row, chapter);
            }
        }
    }

    /// <summary>F-OUT-5: a row id used more than once in a series' outlines, or in a standalone book's outline.</summary>
    private static IEnumerable<DenDiagnostic> Repeats(Container group, IReadOnlyDictionary<string, List<RowPlace>> rows)
    {
        var where = group is Series series ? $"the outlines of series '{series.Key}'" : $"the outline of book '{group.Key}'";
        foreach (var (rowId, places) in rows.Where(r => r.Value.Count > 1).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            foreach (var place in places)
            {
                var others = places.Where(p => p != place).Select(p => p.Path == place.Path ? $"line {p.Line}" : $"{p.Path}:{p.Line}");
                yield return new DenDiagnostic(place.Path, new Diagnostic("F-OUT-5", DiagnosticSeverity.Warning, place.Line,
                    $"Row id {rowId} is used more than once in {where} (also {string.Join(", ", others)}). Row ids should be unique, and a row field naming {rowId} is ambiguous until they are."));
            }
        }
    }

    /// <summary>F-OUT-6: every row of a book comes after every row of the book before it in the series (the nearest earlier one with rows).</summary>
    private static IEnumerable<DenDiagnostic> Counting(Series series, Dictionary<Book, (string Path, IReadOnlyList<OutlineRow> Rows)> read)
    {
        (Book Book, OutlineRow Highest)? before = null;
        foreach (var book in series.Books)
        {
            if (!read.TryGetValue(book, out var outline) || outline.Rows.Count == 0)
            {
                continue;
            }

            var lowest = outline.Rows.MinBy(r => r.RowId, Comparer<string>.Create(OutlineRow.CompareIds))!;
            var highest = outline.Rows.MaxBy(r => r.RowId, Comparer<string>.Create(OutlineRow.CompareIds))!;
            if (before is { } previous && OutlineRow.CompareIds(lowest.RowId, previous.Highest.RowId) <= 0)
            {
                yield return new DenDiagnostic(outline.Path, new Diagnostic("F-OUT-6", DiagnosticSeverity.Warning, lowest.Line,
                    $"Row {lowest.RowId}, the lowest in book '{book.Key}', isn't after row {previous.Highest.RowId}, the highest in book '{previous.Book.Key}' before it. Row ids keep counting across a series, so every row of a book should come after the rows of the book before it."));
            }

            before = (book, highest);
        }
    }
}
