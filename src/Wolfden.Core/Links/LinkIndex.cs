using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Links;

/// <summary>A link and what it resolved to.</summary>
public sealed class ResolvedLink
{
    internal ResolvedLink(LinkOccurrence occurrence, TargetResolution resolution, Documents.Section? targetSection)
    {
        Occurrence = occurrence;
        Resolution = resolution;
        TargetSection = targetSection;
    }

    /// <summary>The link and where it's written.</summary>
    public LinkOccurrence Occurrence { get; }

    /// <summary>How the target resolved.</summary>
    public TargetResolution Resolution { get; }

    /// <summary>Resolved, ambiguous, or broken.</summary>
    public LinkOutcome Outcome => Resolution.Outcome;

    /// <summary>The entity the link resolves to, or null.</summary>
    public CatalogEntity? Target => Resolution.Entity;

    /// <summary>For a <c>#heading</c> link, the section it matched (F-LINK-9), or null.</summary>
    public Documents.Section? TargetSection { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Occurrence} -> {Target?.ToString() ?? Outcome.ToString()}";
}

/// <summary>
/// Every link in a den, resolved and checked (format.md §10): entity
/// bodies and fields, container bodies, chapter fields and
/// <c>chapter_defaults</c>, and outlines. It reports empty links
/// (F-LINK-3), ambiguous (F-LINK-7) and broken (F-LINK-8) links, headings
/// that don't match (F-LINK-9), links from canon to idea or retconned
/// entities (F-LINK-12), and, when the den turns on spoiler checks
/// (F-DEN-7), links that run ahead of their position (F-LINK-13).
/// </summary>
/// <remarks>
/// The type check on <c>ref</c> fields (F-LINK-11) needs the den's types,
/// which aren't loaded yet, so it's left to <c>wolfden check</c> (#16).
/// </remarks>
public sealed class LinkIndex
{
    private readonly List<ResolvedLink> _links = [];
    private readonly Dictionary<CatalogEntity, List<ResolvedLink>> _backlinks = [];
    private readonly List<DenDiagnostic> _diagnostics = [];

    private LinkIndex(EntityCatalog catalog)
    {
        Catalog = catalog;
        Resolver = new LinkResolver(catalog);
    }

    /// <summary>The entities the links resolve against.</summary>
    public EntityCatalog Catalog { get; }

    /// <summary>The resolver, for resolving other targets against the same entities.</summary>
    public LinkResolver Resolver { get; }

    /// <summary>Every link, by file path and then position in the file.</summary>
    public IReadOnlyList<ResolvedLink> Links => _links;

    /// <summary>Link problems, by path and then line.</summary>
    public IReadOnlyList<DenDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Loads the den at <paramref name="root"/> and resolves its links.</summary>
    public static LinkIndex Load(string root) => Load(new FileSystemDenSource(root));

    /// <summary>Loads a den and resolves its links.</summary>
    public static LinkIndex Load(IDenSource source) => Build(EntityCatalog.Load(source), source);

    /// <summary>
    /// Finds and resolves every link in the den. Files that weren't loaded,
    /// because they or a container above them have errors (F-GEN-2), are
    /// skipped. <paramref name="source"/> is read only for <c>outline.md</c> files.
    /// </summary>
    public static LinkIndex Build(EntityCatalog catalog, IDenSource source)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        var index = new LinkIndex(catalog);
        index.Run(source);
        return index;
    }

    /// <summary>The links that resolve to <paramref name="entity"/>: its backlinks.</summary>
    public IReadOnlyList<ResolvedLink> LinksTo(CatalogEntity entity) => _backlinks.TryGetValue(entity, out var links) ? links : [];

    private void Run(IDenSource source)
    {
        var structure = Catalog.Structure;
        var occurrences = new List<LinkOccurrence>();
        var scanner = new LinkScanner(new Bookkeeping(structure.BookkeepingSections), occurrences, _diagnostics);
        foreach (var entity in Catalog.Entities)
        {
            scanner.ScanEntity(entity);
        }

        foreach (var universe in structure.Universes)
        {
            foreach (var container in new Container[] { universe }.Concat(universe.Series).Concat(universe.Books))
            {
                scanner.ScanContainer(container);
            }

            foreach (var book in universe.Books)
            {
                var path = $"{book.Path}/outline.md";
                if (!source.FileExists(path))
                {
                    continue;
                }

                var outline = MarkdownFile.Parse(source.ReadFile(path));
                // F-GEN-2: an error excludes the outline. Errors in section
                // comments don't, since wolfden comments do nothing in an outline (F-SEC-10).
                var excluded = outline.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error
                    && (d.RuleId is "F-ENC-1" or "F-ENC-3" or "F-FM-1" or "F-FM-2" || d.RuleId.StartsWith("F-YAML-", StringComparison.Ordinal)));
                if (!excluded)
                {
                    scanner.ScanOutline(book, path, outline);
                }
            }
        }

        foreach (var occurrence in occurrences.OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Link.Line).ThenBy(o => o.Link.Column))
        {
            var link = Check(occurrence, structure.SpoilerChecks);
            _links.Add(link);
            if (link.Target is { } target)
            {
                if (!_backlinks.TryGetValue(target, out var list))
                {
                    _backlinks[target] = list = [];
                }

                list.Add(link);
            }
        }

        var sorted = _diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Diagnostic.Line ?? 0).ToList();
        _diagnostics.Clear();
        _diagnostics.AddRange(sorted);
    }

    private ResolvedLink Check(LinkOccurrence occurrence, bool spoilerChecks)
    {
        var link = occurrence.Link;
        var resolution = Resolver.Resolve(occurrence.Scope, link.Target);
        if (resolution.Entity is not { } target)
        {
            Warn(occurrence, resolution.Outcome == LinkOutcome.Ambiguous ? "F-LINK-7" : "F-LINK-8", $"{link.Text}: {resolution.Problem}");
            return new ResolvedLink(occurrence, resolution, null);
        }

        Documents.Section? section = null;
        if (link.Heading is { } heading)
        {
            var wanted = LinkText.Normalize(heading);
            section = target.File.Sections.FirstOrDefault(s => LinkText.Normalize(s.Name) == wanted);
            if (section is null)
            {
                Warn(occurrence, "F-LINK-9", $"{link.Text}: '{target.Name}' has no section '{heading}', so the link goes to the entity.");
            }
        }

        if (!occurrence.IsBookkeeping)
        {
            if (occurrence.IsInCanonFact && target.Status is FactStatus.Idea or FactStatus.Retconned)
            {
                Warn(occurrence, "F-LINK-12",
                    $"{link.Text}: this canon text links to '{target.Name}', whose status is {target.Status.ToKey()}.");
            }

            if (spoilerChecks && target.EntersCanon is { } enters)
            {
                foreach (var position in occurrence.Positions)
                {
                    if (!position.Point.IsStartOfStory && enters > position.Point)
                    {
                        var where = position.Chapter is { } chapter
                            ? $"chapter {chapter}, which inherits this default"
                            : $"this link's position, {position.Description}";
                        Warn(occurrence, "F-LINK-13",
                            $"{link.Text}: '{target.Name}' enters canon at {target.EntersCanonDescription}, after {where}. It may be a spoiler.");
                    }
                }
            }
        }

        return new ResolvedLink(occurrence, resolution, section);
    }

    private void Warn(LinkOccurrence occurrence, string rule, string message) =>
        _diagnostics.Add(new DenDiagnostic(occurrence.Path, new Diagnostic(rule, DiagnosticSeverity.Warning, occurrence.Link.Line, message)));
}
