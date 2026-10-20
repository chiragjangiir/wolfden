using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Checks;

/// <summary>
/// The layout warnings of <c>wolfden check</c> (format.md §5.1): a
/// <c>den.yaml</c> below the den root (F-LAY-1), the reserved
/// <c>notes/</c> and <c>authors/</c> folders (F-LAY-3), an entity outside
/// its type's folder (F-LAY-6), and a stray <c>.md</c> file in a container
/// folder (F-LAY-7). Hidden files and folders are never looked at (F-LAY-2).
/// </summary>
internal static class LayoutChecks
{
    /// <summary>How deep the search for a nested <c>den.yaml</c> goes, so a folder link that loops can't make it run forever.</summary>
    private const int MaxDepth = 32;

    public static List<DenDiagnostic> Run(EntityCatalog catalog, IDenSource source)
    {
        var diagnostics = new List<DenDiagnostic>();
        foreach (var path in NestedDenFiles(source, "", 0))
        {
            diagnostics.Add(new DenDiagnostic(path, new Diagnostic("F-LAY-1", DiagnosticSeverity.Warning, null,
                "Dens don't nest, so this den.yaml is ignored: its folder is part of the den around it. Move the folder out of this den to make it a den of its own.")));
        }

        foreach (var folder in source.GetDirectories("").Where(n => n is "notes" or "authors"))
        {
            diagnostics.Add(new DenDiagnostic(folder, new Diagnostic("F-LAY-3", DiagnosticSeverity.Warning, null,
                $"{folder}/ at the den root is reserved for {(folder == "notes" ? "den notes" : "author notes")}, which aren't supported yet (#45), so what's in it is ignored. Keep notes under a universe, series, or book, in entities/notes/.")));
        }

        foreach (var universe in catalog.Structure.Universes)
        {
            foreach (var container in new Container[] { universe }.Concat(universe.Series).Concat(universe.Books))
            {
                diagnostics.AddRange(StrayFiles(container, source));
            }
        }

        foreach (var entity in catalog.Entities)
        {
            if (TypeFolder(entity) is { } diagnostic)
            {
                diagnostics.Add(new DenDiagnostic(entity.Path, diagnostic));
            }
        }

        return diagnostics;
    }

    /// <summary>F-LAY-1: every <c>den.yaml</c> below the root, at any depth, skipping hidden names.</summary>
    private static IEnumerable<string> NestedDenFiles(IDenSource source, string folder, int depth)
    {
        if (depth > 0 && source.GetFiles(folder).Contains("den.yaml", StringComparer.Ordinal))
        {
            yield return $"{folder}/den.yaml";
        }

        if (depth >= MaxDepth)
        {
            yield break;
        }

        foreach (var name in source.GetDirectories(folder).Where(n => !n.StartsWith('.')))
        {
            foreach (var path in NestedDenFiles(source, folder.Length == 0 ? name : $"{folder}/{name}", depth + 1))
            {
                yield return path;
            }
        }
    }

    /// <summary>F-LAY-7: a <c>.md</c> file directly in a container folder, other than its container file and a book's <c>outline.md</c>.</summary>
    private static IEnumerable<DenDiagnostic> StrayFiles(Container container, IDenSource source)
    {
        var own = Container.FileName(container.Kind);
        foreach (var name in source.GetFiles(container.Path))
        {
            if (name.StartsWith('.') || !name.EndsWith(".md", StringComparison.Ordinal) || name == own
                || (name == "outline.md" && container is Book))
            {
                continue;
            }

            var message = name == "outline.md"
                ? $"An outline is read only in a book folder, so this outline.md in {Kind(container)} '{container.Key}' is ignored. Move it into one of its books."
                : $"This file is ignored: the only notes Wolfden reads in a {Kind(container)} folder are {own}{(container is Book ? " and outline.md" : "")}. If it's a note, move it into entities/, in its type's folder.";
            yield return new DenDiagnostic($"{container.Path}/{name}", new Diagnostic("F-LAY-7", DiagnosticSeverity.Warning, null, message));
        }
    }

    /// <summary>
    /// F-LAY-6: the first folder under <c>entities/</c> is the plural of the
    /// entity's type. A file directly in <c>entities/</c>, or in the folder of
    /// another enabled type, is a warning. A folder that is no type's (such
    /// as <c>entities/crew/</c>) isn't, and nor is an untyped entity (F-ENT-2).
    /// </summary>
    private static Diagnostic? TypeFolder(CatalogEntity entity)
    {
        if (entity.Definition is not { } type)
        {
            return null;
        }

        var rest = entity.Path[($"{entity.Scope.Path}/entities/".Length)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var line = entity.File.Frontmatter?["type"]?.Line;
        if (slash < 0)
        {
            return new Diagnostic("F-LAY-6", DiagnosticSeverity.Warning, line,
                $"This {type.Key} is directly in entities/. Notes go in their type's folder: move it to entities/{type.Plural}/.");
        }

        var folder = rest[..slash];
        if (folder == type.Plural)
        {
            return null;
        }

        var other = entity.Universe.Den.Types.Types.FirstOrDefault(t => t.Plural == folder);
        return other is null
            ? null
            : new Diagnostic("F-LAY-6", DiagnosticSeverity.Warning, line,
                $"This {type.Key} is in entities/{folder}/, the folder for type '{other.Key}'. Its type decides what it is, not its folder, but it belongs in entities/{type.Plural}/.");
    }

    private static string Kind(Container container) => container.Kind.ToString().ToLowerInvariant();
}
