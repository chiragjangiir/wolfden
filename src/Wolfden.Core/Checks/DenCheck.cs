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
/// continuity checks that only <c>check</c> runs. It reads the files
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

    /// <summary>Checks a den. Never throws for bad content: problems come back as <see cref="Diagnostics"/>.</summary>
    public static DenCheck Run(IDenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var structure = DenStructure.Load(source);
        var catalog = EntityCatalog.Load(structure, source);
        var links = LinkIndex.Build(catalog, source);
        var diagnostics = structure.Diagnostics
            .Concat(catalog.Diagnostics)
            .Concat(links.Diagnostics)
            .Concat(ContinuityChecks.Run(catalog))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Line ?? 0)
            .ThenBy(d => d.Diagnostic.RuleId, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Message, StringComparer.Ordinal)
            .ToList();
        return new DenCheck(structure, catalog, links, diagnostics);
    }
}
