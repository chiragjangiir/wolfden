using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Checks;

/// <summary>
/// The result of <c>wolfden check</c> (design.md §7): every diagnostic in a
/// den. It loads the den the way the index does, so it reports the same
/// errors and warnings, from <see cref="DenStructure"/>,
/// <see cref="EntityCatalog"/>, and <see cref="LinkIndex"/>, and adds the
/// checks that only <c>check</c> runs: continuity and types
/// (<see cref="ContinuityChecks"/>, <see cref="FieldChecks"/>), outlines
/// (<see cref="OutlineChecks"/>), and layout (<see cref="LayoutChecks"/>). It reads the files
/// directly, never the index, and never writes (F-ID-6).
/// </summary>
public sealed class DenCheck
{
    private DenCheck(DenStructure structure, EntityCatalog catalog, LinkIndex links, IReadOnlyList<DenDiagnostic> diagnostics)
    {
        Structure = structure;
        Catalog = catalog;
        Links = links;
        Diagnostics = diagnostics;
    }

    /// <summary>The den's structure.</summary>
    public DenStructure Structure { get; }

    /// <summary>The den's entities.</summary>
    public EntityCatalog Catalog { get; }

    /// <summary>The den's links.</summary>
    public LinkIndex Links { get; }

    /// <summary>
    /// Every diagnostic (F-GEN-3), sorted by path in ASCII order, then by
    /// line (problems with no line first), then by rule id and message, so
    /// the same files always give the same list.
    /// </summary>
    public IReadOnlyList<DenDiagnostic> Diagnostics { get; }

    /// <summary>True if <c>den.yaml</c> loaded (F-DEN-1, F-GEN-2). If not, nothing else was checked.</summary>
    public bool IsLoaded => Structure.IsLoaded;

    /// <summary>The number of errors.</summary>
    public int ErrorCount => Diagnostics.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>The number of warnings.</summary>
    public int WarningCount => Diagnostics.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Warning);

    /// <summary>Checks the den whose root folder is <paramref name="root"/>.</summary>
    public static DenCheck Run(string root) => Run(new FileSystemDenSource(root));

    /// <summary>
    /// Checks one universe of the den at <paramref name="root"/>, or the whole
    /// den when <paramref name="universeKey"/> is null. See <see cref="Run(IDenSource, string?)"/>.
    /// </summary>
    public static DenCheck Run(string root, string? universeKey, IReadOnlyDictionary<string, IReadOnlyList<string>>? idsElsewhere = null) =>
        Run(new FileSystemDenSource(root), universeKey, idsElsewhere);

    /// <summary>
    /// Checks one universe of a den (maintainer decision, 2026-10-23): its
    /// files and the den-level material every universe shares (<c>den.yaml</c>,
    /// packs, authors, and the den's own layout). The other universes' notes
    /// and outlines aren't read; their container files are, so story order
    /// and positions resolve as in the whole den, but their findings are left
    /// out. <paramref name="idsElsewhere"/> gives, for each id, the paths of
    /// the other universes' entity files that use it (from the index, say):
    /// with it, an id shared with a note in another universe is a duplicate
    /// (F-ID-3), and links break, exactly as in a check of the whole den.
    /// Without it, those duplicates aren't found. Either way, a broken link
    /// that only another universe's note matches is reported without the
    /// hint that names that note. A null <paramref name="universeKey"/>
    /// checks the whole den, as <see cref="Run(IDenSource)"/> does. The
    /// caller checks that the universe exists.
    /// </summary>
    public static DenCheck Run(IDenSource source, string? universeKey, IReadOnlyDictionary<string, IReadOnlyList<string>>? idsElsewhere = null)
    {
        if (universeKey is null)
        {
            return Run(source);
        }

        var scoped = new UniverseScopedSource(source, universeKey);
        var check = Run(scoped, idsElsewhere);
        return new DenCheck(
            check.Structure, check.Catalog, check.Links, check.Diagnostics.Where(d => !scoped.IsInOtherUniverse(d.Path)).ToList());
    }

    /// <summary>Checks a den. Never throws for bad content: problems come back as <see cref="Diagnostics"/>.</summary>
    public static DenCheck Run(IDenSource source) => Run(source, idsElsewhere: null);

    private static DenCheck Run(IDenSource source, IReadOnlyDictionary<string, IReadOnlyList<string>>? idsElsewhere)
    {
        ArgumentNullException.ThrowIfNull(source);
        var structure = DenStructure.Load(source);
        var catalog = EntityCatalog.Load(structure, source, idsElsewhere);
        var links = LinkIndex.Build(catalog, source);
        var outlines = OutlineChecks.Run(structure, source, out var rows);
        var diagnostics = structure.Diagnostics
            .Concat(catalog.Diagnostics)
            .Concat(links.Diagnostics)
            .Concat(outlines)
            .Concat(structure.IsLoaded ? LayoutChecks.Run(catalog, source) : [])
            .Concat(ContinuityChecks.Run(catalog, source, rows))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Line ?? 0)
            .ThenBy(d => d.Diagnostic.RuleId, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Message, StringComparer.Ordinal)
            .ToList();
        return new DenCheck(structure, catalog, links, diagnostics);
    }
}
