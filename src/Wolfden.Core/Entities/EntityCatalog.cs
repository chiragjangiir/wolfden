using System.Text.RegularExpressions;
using Wolfden.Core.Documents;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Entities;

/// <summary>
/// The entities of a den: every entity file under an <c>entities/</c>
/// folder of a loaded container (format.md F-LAY-5), read into
/// <see cref="CatalogEntity"/> records. It's the in-memory model that link
/// resolution works against and that the index can persist. It never
/// writes (F-ID-6).
/// </summary>
/// <remarks>
/// <para>
/// An entity file with an error is left out (F-GEN-2), and the error is in
/// <see cref="Diagnostics"/>. The catalog checks what decides whether an
/// entity loads and how links find it: the file's parse errors, a bad
/// <c>from</c> or <c>until</c>, <c>id</c> (F-ID-1), <c>type</c> (F-ENT-2),
/// <c>name</c> (F-ENT-3), <c>aliases</c> (F-ENT-4), <c>status</c> (F-ENT-5),
/// and the shape of <c>fields</c> and their entries (F-ENT-7, F-FLD-1,
/// F-TMP-1, F-TMP-2, F-STAT-1, F-PIN-1). It also reports the warnings that
/// change how links reach an entity: a missing id (F-ID-2), a duplicate id
/// (F-ID-3), and a file name that isn't a valid slug (F-KEY-4).
/// </para>
/// <para>
/// Each entity's type is looked up among the den's enabled types
/// (<see cref="CatalogEntity.Definition"/>). Checks against types and
/// checks that only warn, such as unknown types, missing required fields
/// and sections, and positions that don't resolve, are made by
/// <see cref="Checks.DenCheck"/> (<c>wolfden check</c>). Field values
/// aren't checked against their field types yet (F-FLD-2, F-FLD-3).
/// </para>
/// </remarks>
public sealed partial class EntityCatalog
{
    private readonly List<CatalogEntity> _entities = [];
    private readonly List<DenDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, CatalogEntity> _byPath = new(StringComparer.Ordinal);
    private readonly List<ExcludedEntity> _excluded = [];

    private EntityCatalog(DenStructure structure)
    {
        Structure = structure;
    }

    /// <summary>The den's structure, which the entities' scopes and positions refer to.</summary>
    public DenStructure Structure { get; }

    /// <summary>Every entity that loaded, by path in ASCII order.</summary>
    public IReadOnlyList<CatalogEntity> Entities => _entities;

    /// <summary>Problems with entity files, by path and then line.</summary>
    public IReadOnlyList<DenDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Loads the den whose root folder is <paramref name="root"/>: its structure, then its entities.</summary>
    public static EntityCatalog Load(string root) => Load(new FileSystemDenSource(root));

    /// <summary>Loads a den's structure, then its entities.</summary>
    public static EntityCatalog Load(IDenSource source) => Load(DenStructure.Load(source), source);

    /// <summary>Loads the entities of a den whose structure is already loaded.</summary>
    public static EntityCatalog Load(DenStructure structure, IDenSource source)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(source);
        var catalog = new EntityCatalog(structure);
        catalog.LoadEntities(source);
        return catalog;
    }

    /// <summary>The entities of <paramref name="universe"/>, at every scope.</summary>
    public IEnumerable<CatalogEntity> InUniverse(Universe universe) => _entities.Where(e => e.Universe == universe);

    /// <summary>The entity whose file is at <paramref name="path"/>, or null.</summary>
    public CatalogEntity? FindByPath(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>Entity files left out for errors, with the texts a link could have matched them by.</summary>
    internal IReadOnlyList<ExcludedEntity> ExcludedEntities => _excluded;

    private void LoadEntities(IDenSource source)
    {
        var containers = Structure.Universes
            .SelectMany(u => new Container[] { u }.Concat(u.Series).Concat(u.Books))
            .ToList();
        foreach (var container in containers)
        {
            foreach (var path in EntityFiles(source, $"{container.Path}/entities").Order(StringComparer.Ordinal))
            {
                var diagnostics = new List<Diagnostic>();
                var entity = Read(path, source.ReadFile(path), container, diagnostics, _excluded);
                foreach (var d in diagnostics)
                {
                    _diagnostics.Add(new DenDiagnostic(path, d));
                }

                if (entity is not null)
                {
                    _entities.Add(entity);
                    _byPath[path] = entity;
                }
            }
        }

        _entities.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        FindDuplicateIds(containers);
        var sorted = _diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Diagnostic.Line ?? 0).ToList();
        _diagnostics.Clear();
        _diagnostics.AddRange(sorted);
    }

    /// <summary>Every <c>.md</c> file under <paramref name="folder"/>, at any depth, skipping hidden names (F-LAY-2).</summary>
    private static IEnumerable<string> EntityFiles(IDenSource source, string folder)
    {
        foreach (var name in source.GetFiles(folder))
        {
            if (!name.StartsWith('.') && name.EndsWith(".md", StringComparison.Ordinal))
            {
                yield return $"{folder}/{name}";
            }
        }

        foreach (var name in source.GetDirectories(folder))
        {
            if (!name.StartsWith('.'))
            {
                foreach (var path in EntityFiles(source, $"{folder}/{name}"))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// F-ID-3: an id used by more than one entity or container file is a
    /// warning on each of them, and those entities are left out of link
    /// resolution.
    /// </summary>
    private void FindDuplicateIds(List<Container> containers)
    {
        var owners = _entities.Where(e => e.Id is not null).Select(e => (Id: e.Id!, e.Path, Entity: (CatalogEntity?)e))
            .Concat(containers.Where(c => c.Id is not null).Select(c => (Id: c.Id!, Path: c.FilePath, Entity: (CatalogEntity?)null)));
        foreach (var group in owners.GroupBy(o => o.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var paths = group.Select(o => o.Path).Order(StringComparer.Ordinal).ToList();
            foreach (var owner in group)
            {
                if (owner.Entity is { } entity)
                {
                    entity.HasDuplicateId = true;
                }

                var line = owner.Entity?.File.Frontmatter?["id"]?.Line;
                _diagnostics.Add(new DenDiagnostic(owner.Path, new Diagnostic("F-ID-3", DiagnosticSeverity.Warning, line,
                    $"The id {group.Key} is used by {paths.Count} files ({string.Join(", ", paths)}). Until it's unique, links that would reach any of them are broken. Give each copy a new id.")));
            }
        }
    }

    private static CatalogEntity? Read(string path, byte[] bytes, Container scope, List<Diagnostic> diagnostics, List<ExcludedEntity> excluded)
    {
        var file = MarkdownFile.Parse(bytes, requireFrontmatter: true);
        diagnostics.AddRange(file.Diagnostics);
        var fm = file.Frontmatter;
        if (fm is null)
        {
            // No frontmatter, or frontmatter that isn't a mapping: already an error.
            return null;
        }

        var errors = new List<Diagnostic>();
        void Error(string rule, int line, string message) => errors.Add(new Diagnostic(rule, DiagnosticSeverity.Error, line, message));

        string? id = null;
        switch (fm["id"])
        {
            case null or YamlScalar { IsNull: true }:
                diagnostics.Add(new Diagnostic("F-ID-2", DiagnosticSeverity.Warning, 1,
                    "no id; run `wolfden check --fix` to add one. Until then, links can't reach this entity by id."));
                break;
            case YamlScalar s when Keys.IsId(s.Text):
                id = s.Text;
                break;
            case var node:
                Error("F-ID-1", node.Line, $"'{Describe(node)}' isn't a valid id: an id is a ULID, 26 uppercase Crockford base32 characters.");
                break;
        }

        var type = fm["type"] is YamlScalar { IsNull: false } t && t.Text.Trim().Length > 0 ? t.Text : null;
        if (type is null)
        {
            Error("F-ENT-2", fm["type"]?.Line ?? 1, "An entity needs a type, such as 'type: character'.");
        }

        var name = fm["name"] is YamlScalar { IsNull: false } n && n.Text.Trim().Length > 0 ? n.Text : null;
        if (name is null)
        {
            Error("F-ENT-3", fm["name"]?.Line ?? 1, "An entity needs a non-empty name.");
        }

        var aliases = new List<string>();
        switch (fm["aliases"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlSequence list:
                foreach (var item in list.Items)
                {
                    if (item is YamlScalar { IsNull: false } a && a.Text.Trim().Length > 0)
                    {
                        aliases.Add(a.Text);
                    }
                    else
                    {
                        Error("F-ENT-4", item.Line, "Each alias must be a non-empty string.");
                    }
                }

                break;
            case var node:
                Error("F-ENT-4", node.Line, "aliases must be a list of strings, such as 'aliases: [Voss]', even for one alias.");
                break;
        }

        var status = FactStatus.Draft;
        switch (fm["status"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar s when FactStatuses.Parse(s.Text) is { } parsed:
                status = parsed;
                break;
            case var node:
                Error("F-ENT-5", node.Line,
                    $"'{Describe(node)}' isn't a status. Use idea, undecided, candidate, draft, canon, or retconned.");
                break;
        }

        CheckFields(fm["fields"], Error);

        // Syntax errors in from and until (F-POS-2, F-POS-3, F-POS-6).
        errors.AddRange(PositionScanner.Scan(file).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        diagnostics.AddRange(errors);

        var fileName = path[(path.LastIndexOf('/') + 1)..^3];
        var slug = Keys.IsKey(fileName) ? fileName : null;
        if (slug is null)
        {
            diagnostics.Add(new Diagnostic("F-KEY-4", DiagnosticSeverity.Warning, null,
                $"The file name '{fileName}.md' isn't a valid slug (lowercase letters and digits, with single hyphens between words), so links reach this entity only by id, name, or alias."));
        }

        if (file.HasErrors || errors.Count > 0)
        {
            // Kept only so a broken link can say the file it would reach has errors.
            var keys = new List<string>();
            if (fm["id"] is YamlScalar { IsNull: false } rawId)
            {
                keys.Add(rawId.Text);
            }

            keys.AddRange(new[] { slug, fm["name"] is YamlScalar { IsNull: false } rawName ? rawName.Text : null }
                .Concat((fm["aliases"] as YamlSequence)?.Items.OfType<YamlScalar>().Where(a => !a.IsNull).Select(a => a.Text) ?? [])
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => Links.LinkText.Normalize(k!)));
            excluded.Add(new ExcludedEntity(path, scope.Universe, keys));
            return null;
        }

        StoryPosition? from = null;
        PositionResolution? fromResolution = null;
        if (fm["from"] is YamlScalar { IsNull: false } f)
        {
            from = StoryPosition.Parse(f.Text);
            fromResolution = scope.Universe.Resolve(from);
        }

        return new CatalogEntity(path, file, scope, id, slug, name!, aliases, type!, status, from, fromResolution)
        {
            Definition = scope.Universe.Den.Types.Find(type!),
        };
    }

    /// <summary>F-ENT-7, F-FLD-1, F-TMP-1, F-TMP-2, and the status and pin of entries (F-STAT-1, F-PIN-1).</summary>
    internal static void CheckFields(YamlNode? fields, Action<string, int, string> error)
    {
        if (fields is null or YamlScalar { IsNull: true })
        {
            return;
        }

        if (fields is not YamlMapping map)
        {
            error("F-ENT-7", fields.Line, "fields must be a mapping from field name to value.");
            return;
        }

        foreach (var entry in map.Entries)
        {
            if (entry.KeyText is not { } fieldName || !FieldName().IsMatch(fieldName))
            {
                error("F-FLD-1", entry.Key.Line,
                    $"'{entry.KeyText}' isn't a valid field name: use lowercase letters, digits, and '_', starting with a letter, up to 64 characters.");
            }

            var value = entry.Value;
            if (value is YamlSequence list)
            {
                var entries = list.Items.Count(IsEntry);
                if (entries > 0 && entries < list.Items.Count)
                {
                    error("F-TMP-1", list.Line, $"'{entry.KeyText}' mixes entries (mappings with a value) and plain items. Make every item an entry, or none.");
                    continue;
                }

                foreach (var item in list.Items.OfType<YamlMapping>().Where(IsEntry))
                {
                    CheckEntry(item, error);
                }
            }
            else if (value is YamlMapping single && IsEntry(single))
            {
                CheckEntry(single, error);
            }
        }
    }

    private static void CheckEntry(YamlMapping entry, Action<string, int, string> error)
    {
        var value = entry["value"];
        if (value is YamlMapping inner && IsEntry(inner) || value is YamlSequence { Items.Count: > 0 } list && list.Items.All(IsEntry))
        {
            error("F-TMP-2", value.Line, "An entry's value can't itself be an entry or a list of entries.");
        }

        switch (entry["status"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar s when FactStatuses.Parse(s.Text) is not null:
                break;
            case var node:
                error("F-STAT-1", node.Line, $"'{Describe(node)}' isn't a status. Use idea, undecided, candidate, draft, canon, or retconned.");
                break;
        }

        switch (entry["pin"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar s when s.AsBoolean() is not null:
                break;
            case var node:
                error("F-PIN-1", node.Line, $"pin must be true or false, not '{Describe(node)}'.");
                break;
        }
    }

    /// <summary>An entry is a mapping with a <c>value</c> key (F-TMP-1).</summary>
    internal static bool IsEntry(YamlNode node) => node is YamlMapping m && m.ContainsKey("value");

    private static string Describe(YamlNode node) => node switch
    {
        YamlScalar s => s.Text,
        YamlSequence => "a list",
        _ => "a mapping",
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldName();
}

/// <summary>An entity file left out for errors (F-GEN-2), kept for broken-link hints.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Universe">The universe it's in.</param>
/// <param name="MatchKeys">Its id as written, and its slug, name, and aliases normalized (F-LINK-4).</param>
internal sealed record ExcludedEntity(string Path, Universe Universe, IReadOnlyList<string> MatchKeys);
