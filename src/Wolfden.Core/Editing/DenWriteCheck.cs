using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Editing;

/// <summary>
/// Checks files a writer is about to save, as the den would read them once
/// saved (format.md §1.3: the app and the MCP tools must refuse to write a
/// file that would have an error). Each file is read the way a load of the
/// den reads it, through an <see cref="OverlayDenSource"/>, so the errors
/// are exactly the ones the parser and <c>wolfden check</c> would report.
/// Errors depend only on the file itself, <c>den.yaml</c>, and the enabled
/// types (F-GEN-4), so the rest of the den needn't be read again.
/// </summary>
public static class DenWriteCheck
{
    /// <summary>
    /// Reads <paramref name="files"/> (den-relative path to new contents) as
    /// the den would: container files by loading the den's structure over
    /// them, outlines as outlines, and entity files as the entity catalog
    /// does, with the field checks of <c>wolfden check</c> for warnings.
    /// </summary>
    /// <param name="den">The den as it is now.</param>
    /// <param name="structure">The den's structure as it is now.</param>
    /// <param name="files">The files to check, by den-relative path.</param>
    /// <exception cref="ArgumentException">A path isn't a container file, an outline, or an entity file of the den: writers don't edit anything else.</exception>
    public static DenWriteCheckResult Check(IDenSource den, DenStructure structure, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(files);
        var overlay = new OverlayDenSource(den, files);
        var containers = Containers(structure).ToDictionary(c => c.FilePath, StringComparer.Ordinal);
        var after = files.Keys.Any(containers.ContainsKey) ? DenStructure.Load(overlay) : structure;
        var outlines = after.Universes.SelectMany(u => u.Books).ToDictionary(b => $"{b.Path}/outline.md", StringComparer.Ordinal);
        RowIndex? rows = null;

        var result = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            if (containers.ContainsKey(path))
            {
                result[path] = after.Diagnostics.Where(d => d.Path == path).Select(d => d.Diagnostic).ToList();
            }
            else if (outlines.ContainsKey(path))
            {
                var outline = MarkdownFile.ParseOutline(bytes);
                result[path] = outline.Diagnostics.Concat(Outlines.OutlineReader.Read(outline).Diagnostics).OrderBy(d => d.Line ?? 0).ToList();
            }
            else if (EntityCatalog.EntityFileScope(after, path) is not null)
            {
                var read = EntityCatalog.ReadEntityFile(after, path, bytes);
                var found = read.Diagnostics.ToList();
                if (read.Entity is { } entity)
                {
                    if (rows is null)
                    {
                        OutlineChecks.Run(after, overlay, out rows);
                    }

                    FieldChecks.Run(entity, rows, found);
                }

                result[path] = found.OrderBy(d => d.Line ?? 0).ToList();
            }
            else if (EntityCatalog.EntityFileScope(structure, path) is not null
                || structure.Universes.SelectMany(u => u.Books).Any(b => path == $"{b.Path}/outline.md"))
            {
                // Its container would no longer load: the error is the container file's.
                result[path] = [];
            }
            else
            {
                throw new ArgumentException($"'{path}' isn't a container file, an outline, or an entity file of the den, so it isn't checked here.", nameof(files));
            }
        }

        return new DenWriteCheckResult(after, result);
    }

    private static IEnumerable<Container> Containers(DenStructure structure) =>
        structure.Universes.SelectMany(u => new Container[] { u }.Concat(u.Series).Concat(u.Books));
}

/// <summary>What <see cref="DenWriteCheck.Check"/> found.</summary>
/// <param name="Structure">The den's structure as it would be after the writes.</param>
/// <param name="Diagnostics">Each file's diagnostics as it would be read, by path.</param>
public sealed record DenWriteCheckResult(DenStructure Structure, IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> Diagnostics)
{
    /// <summary>True if any file would have an error, so it mustn't be written.</summary>
    public bool HasErrors => Diagnostics.Values.Any(list => list.Any(d => d.Severity == DiagnosticSeverity.Error));

    /// <summary>Every error, with its file's path, by path and line.</summary>
    public IReadOnlyList<DenDiagnostic> Errors => All(DiagnosticSeverity.Error);

    /// <summary>Every warning, with its file's path, by path and line.</summary>
    public IReadOnlyList<DenDiagnostic> Warnings => All(DiagnosticSeverity.Warning);

    private List<DenDiagnostic> All(DiagnosticSeverity severity) =>
        Diagnostics.OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => p.Value.Where(d => d.Severity == severity).Select(d => new DenDiagnostic(p.Key, d)))
            .ToList();
}
