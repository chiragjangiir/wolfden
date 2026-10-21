using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Outlines;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Index;

/// <summary>
/// Turns what Core loads from a den into index rows (<see cref="IndexRows"/>).
/// A full build (<see cref="Load"/>) gives ids out in a fixed order
/// (universes, then series and books in story order; entities, links, and
/// diagnostics by path), so building the same files twice gives identical
/// rows. An update (<see cref="Update"/>, #14) builds the rows of the den as
/// it is after some files changed, with the same code, keeping each row's
/// id where its natural key is unchanged (<see cref="IndexIds"/>) and
/// reusing the rows of entities whose files didn't change.
/// </summary>
internal sealed class IndexWriter
{
    // Keep non-ASCII text readable in the database ("Zoë", not "Zoë").
    // The JSON never goes into HTML, so the relaxed encoder is safe here.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _root;
    private readonly IndexState? _previous;
    private readonly IndexIds _ids;
    private readonly IndexRows _rows = new();
    private readonly Dictionary<string, int> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<Container, int> _containers = [];
    private readonly Dictionary<Chapter, int> _chapters = [];
    private readonly Dictionary<CatalogEntity, int> _entities = [];
    private readonly Dictionary<Section, int> _sections = [];
    private readonly Dictionary<string, EntityRows> _entityRows = new(StringComparer.Ordinal);
    private readonly Dictionary<LinkOccurrence, LinkRecord> _linkRows = new(ReferenceEqualityComparer.Instance);

    private IndexWriter(string root, IndexState? previous)
    {
        _root = root;
        _previous = previous;
        _ids = previous is null ? IndexIds.Fresh() : IndexIds.After(previous.Ids);
    }

    /// <summary>
    /// Loads the den at <paramref name="root"/> and builds every row, with
    /// <paramref name="fingerprint"/> (<see cref="IndexBuilder.FilesFingerprint"/>)
    /// and <paramref name="tree"/> taken before anything was read.
    /// </summary>
    public static IndexState Load(string root, DenFileTree tree, string fingerprint)
    {
        var source = new HashingDenSource(new FileSystemDenSource(root));
        var structure = DenStructure.Load(source);
        var catalog = EntityCatalog.Load(structure, source);
        var links = LinkIndex.Build(catalog, source);
        var outlines = ReadOutlines(structure, source, previous: null, changed: new HashSet<string>(StringComparer.Ordinal));
        var hashes = source.Files.ToDictionary(f => f.Path, f => f.Hash, StringComparer.Ordinal);
        if (ForgetMissing(source, tree, hashes))
        {
            fingerprint = tree.Fingerprint();
        }

        return new IndexWriter(root, null).Run(structure, catalog, links, outlines, hashes, source, tree, fingerprint);
    }

    /// <summary>
    /// The state after the entity files <paramref name="changedEntities"/>
    /// and the outlines <paramref name="changedOutlines"/> changed, when the
    /// den's structure didn't: <c>den.yaml</c>, packs, and container files
    /// are the same, and so are the folders that hold them. Unchanged files
    /// aren't read again. The rows are the rows a full build of the den as
    /// it is now would give, apart from their ids.
    /// </summary>
    /// <param name="previous">The state the index holds now.</param>
    /// <param name="source">The den as it is now.</param>
    /// <param name="changedEntities">Paths under an <c>entities/</c> folder that may have been added, changed, or deleted.</param>
    /// <param name="changedOutlines">Paths of <c>outline.md</c> files of loaded books that may have been added, changed, or deleted.</param>
    /// <param name="tree">The den's tree now.</param>
    public static IndexState Update(
        IndexState previous, IDenSource source, IReadOnlySet<string> changedEntities, IReadOnlySet<string> changedOutlines, DenFileTree tree)
    {
        var hashing = new HashingDenSource(source);
        var catalog = previous.Catalog.Update(hashing, changedEntities);
        var links = LinkIndex.Build(catalog, hashing, previous.Links, changedOutlines);
        var outlines = ReadOutlines(previous.Structure, hashing, previous.Outlines, changedOutlines);

        // The files read before, apart from the changed ones, and the changed ones read now.
        var hashes = previous.FileHashes
            .Where(f => !changedEntities.Contains(f.Key) && !changedOutlines.Contains(f.Key))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        foreach (var (path, hash) in hashing.Files)
        {
            hashes[path] = hash;
        }

        ForgetMissing(hashing, tree, hashes);
        return new IndexWriter(previous.Root, previous).Run(previous.Structure, catalog, links, outlines, hashes, source: null, tree, tree.Fingerprint());
    }

    /// <summary>
    /// Takes the files the load found missing or couldn't read (#112) out of
    /// <paramref name="tree"/> and <paramref name="hashes"/>, so the files
    /// fingerprint says what the index was built from. A file that was
    /// hashed for the tree but couldn't be read by the load, say because
    /// another program held it open by then, then makes the index look
    /// stale, so the next query rebuilds it, rather than current while it
    /// lacks the file. Returns true if the tree changed.
    /// </summary>
    private static bool ForgetMissing(HashingDenSource source, DenFileTree tree, Dictionary<string, string> hashes)
    {
        var changed = false;
        foreach (var path in source.Missing)
        {
            hashes.Remove(path);
            if (tree.Hash(path) is not null)
            {
                tree.SetFile(path, null);
                changed = true;
            }
        }

        return changed;
    }

    private IndexState Run(
        DenStructure structure, EntityCatalog catalog, LinkIndex links, IReadOnlyList<OutlineFile> outlines,
        Dictionary<string, string> hashes, IDenSource? source, DenFileTree tree, string fingerprint)
    {
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        if (structure.IsLoaded)
        {
            loaded.Add("den.yaml");
        }

        loaded.UnionWith(LoadedPackFiles(structure, hashes.Keys));
        loaded.UnionWith(AllContainers(structure).Select(c => c.FilePath));
        loaded.UnionWith(catalog.Entities.Select(e => e.Path));
        loaded.UnionWith(outlines.Where(o => o.Rows is not null).Select(o => o.Path));
        foreach (var (path, hash) in hashes.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var id = _ids.Files.Take(path, 0);
            _files[path] = id;
            _rows.Add(new FileRecord { Id = id, Path = path, Kind = FileKind(path), Hash = hash, IsLoaded = loaded.Contains(path) });
        }

        var writeId = Guid.NewGuid().ToString("N");
        if (_previous is null)
        {
            _rows.Meta[IndexSchema.SchemaVersionKey] = IndexSchema.Version.ToString(CultureInfo.InvariantCulture);
            _rows.Meta[IndexSchema.DenFormatKey] = IndexBuilder.DenFormatStamp(source!.TryReadFile("den.yaml"));
            _rows.Meta[IndexSchema.SupportedFormatKey] = DenStructure.SupportedFormat.ToString(CultureInfo.InvariantCulture);
            _rows.Meta[IndexSchema.BuiltInPacksKey] = BuiltInPacks.Instance.Hash;
            _rows.Add(new DenRecord
            {
                Id = 1,
                Name = DenName(source),
                Format = structure.Format,
                IsLoaded = structure.IsLoaded,
                SpoilerChecks = structure.SpoilerChecks,
                BookkeepingSectionsJson = JsonSerializer.Serialize(structure.BookkeepingSections, JsonOptions),
            });
            WriteContainers(structure);
            WriteFieldDefaults(structure);
        }
        else
        {
            // The structure didn't change, so neither did its rows.
            foreach (var (key, value) in _previous.Rows.Meta)
            {
                _rows.Meta[key] = value;
            }

            foreach (var type in new[] { typeof(DenRecord), typeof(ContainerRecord), typeof(ChapterRecord), typeof(FieldDefaultRecord) })
            {
                _rows.ShareTable(type, _previous.Rows);
            }

            foreach (var (container, id) in _previous.ContainerIds)
            {
                _containers[container] = id;
            }

            foreach (var (chapter, id) in _previous.ChapterIds)
            {
                _chapters[chapter] = id;
            }
        }

        _rows.Meta[IndexSchema.FilesFingerprintKey] = fingerprint;
        _rows.Meta[IndexSchema.WriteIdKey] = writeId;

        WriteEntities(catalog, new Bookkeeping(structure.BookkeepingSections));
        WriteLinks(links);
        WriteOutlines(outlines);
        WriteDiagnostics(structure.Diagnostics.Concat(catalog.Diagnostics).Concat(links.Diagnostics));

        return new IndexState
        {
            Root = _root,
            Structure = structure,
            Catalog = catalog,
            Links = links,
            Outlines = outlines,
            FileHashes = hashes,
            Rows = _rows,
            Ids = _ids,
            Entities = _entityRows,
            LinkRows = _linkRows,
            ContainerIds = _containers,
            ChapterIds = _chapters,
            Tree = tree,
            WriteId = writeId,
        };
    }

    private void WriteContainers(DenStructure structure)
    {
        foreach (var universe in structure.Universes)
        {
            var universeId = AddContainer(universe, universe, null, null);
            foreach (var work in universe.Works)
            {
                if (work is Series series)
                {
                    var firstBook = series.Books.Count > 0 ? IndexOf(universe, series.Books[0]) : (int?)null;
                    var seriesId = AddContainer(series, universe, universeId, firstBook);
                    foreach (var book in series.Books)
                    {
                        AddBook(book, universe, universeId, seriesId);
                    }
                }
                else if (work is Book book)
                {
                    AddBook(book, universe, universeId, universeId);
                }
            }
        }
    }

    private void AddBook(Book book, Universe universe, int universeId, int parentId)
    {
        var bookId = AddContainer(book, universe, parentId, IndexOf(universe, book));
        foreach (var chapter in book.Chapters)
        {
            var id = _chapters.Count + 1;
            _chapters[chapter] = id;
            _rows.Add(new ChapterRecord
            {
                Id = id,
                BookId = bookId,
                UniverseId = universeId,
                Key = chapter.Key,
                Index = chapter.Index,
                Number = chapter.Number,
                Title = chapter.Title,
                Line = chapter.Line,
                Ordinal = chapter.Point.Ordinal,
            });
        }
    }

    private int AddContainer(Container container, Universe universe, int? parentId, int? storyOrder)
    {
        var id = _containers.Count + 1;
        _containers[container] = id;
        _rows.Add(new ContainerRecord
        {
            Id = id,
            FileId = _files[container.FilePath],
            Kind = container.Kind.ToString().ToLowerInvariant(),
            UniverseId = container == universe ? id : _containers[universe],
            ParentId = parentId,
            Key = container.Key,
            Name = container.Name,
            Ulid = container.Id,
            Path = container.Path,
            StoryOrder = storyOrder,
            StartOrdinal = container.Start.Ordinal,
            BookStatus = (container as Book)?.Status.ToString().ToLowerInvariant(),
        });
        return id;
    }

    /// <summary>The defaults of every enabled type's fields (types.md F-FT-15), for canon views.</summary>
    private void WriteFieldDefaults(DenStructure structure)
    {
        var id = 0;
        foreach (var type in structure.Types.Types.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            for (var i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                if (field.Default is { } value && value is not YamlScalar { IsNull: true })
                {
                    _rows.Add(new FieldDefaultRecord
                    {
                        Id = ++id,
                        TypeKey = type.Key,
                        Field = field.Name,
                        FieldOrder = i,
                        DefaultJson = Json(value),
                        Path = field.Path,
                        Line = field.Line,
                    });
                }
            }
        }
    }

    private void WriteEntities(EntityCatalog catalog, Bookkeeping bookkeeping)
    {
        foreach (var entity in catalog.Entities)
        {
            // An entity whose file didn't change is the same object, and its
            // rows are the same unless its duplicate-id flag changed.
            if (_previous?.Entities.GetValueOrDefault(entity.Path) is { } kept
                && kept.Entity == entity && kept.HasDuplicateId == catalog.HasDuplicateId(entity))
            {
                foreach (var table in new[] { _ids.Entities, _ids.Aliases, _ids.FieldValues, _ids.Sections, _ids.Search })
                {
                    table.Keep(entity.Path);
                }
            }
            else
            {
                kept = EntityRows(entity, catalog.HasDuplicateId(entity), bookkeeping);
            }

            _entityRows[entity.Path] = kept;
            _entities[entity] = kept.Id;
            foreach (var (section, id) in kept.SectionIds)
            {
                _sections[section] = id;
            }

            foreach (var row in kept.Rows)
            {
                _rows.Add(row);
            }

            foreach (var row in kept.Search)
            {
                _rows.Search.Add(row.RowId, row);
            }
        }
    }

    private EntityRows EntityRows(CatalogEntity entity, bool hasDuplicateId, Bookkeeping bookkeeping)
    {
        var path = entity.Path;
        var rows = new List<object>();
        var search = new List<SearchRow>();
        var sectionIds = new Dictionary<Section, int>();
        var id = _ids.Entities.Take(path, 0);
        var universeId = _containers[entity.Universe];
        var lead = entity.File.Lead;
        rows.Add(new EntityRecord
        {
            Id = id,
            FileId = _files[path],
            UniverseId = universeId,
            ScopeId = _containers[entity.Scope],
            Ulid = entity.Id,
            Key = entity.Key,
            Path = path,
            Slug = entity.Slug,
            Name = entity.Name,
            NameNormalized = LinkText.Normalize(entity.Name),
            Type = entity.Type,
            Status = entity.Status.ToKey(),
            FromText = entity.From?.ToString(),
            FromOrdinal = entity.EntersCanon?.Ordinal,
            HasDuplicateId = hasDuplicateId,
            Title = entity.File.Title,
            Lead = lead,
            LeadLine = entity.File.LeadLine,
        });
        search.Add(Search(path, -1, string.Join("\n", entity.Aliases.Prepend(entity.Name)), lead, "entity", id, null));

        for (var i = 0; i < entity.Aliases.Count; i++)
        {
            rows.Add(new AliasRecord
            {
                Id = _ids.Aliases.Take(path, i),
                EntityId = id,
                Order = i,
                Alias = entity.Aliases[i],
                AliasNormalized = LinkText.Normalize(entity.Aliases[i]),
            });
        }

        var facts = EntityFacts.For(entity, bookkeeping);
        var values = 0;
        foreach (var field in facts.Fields)
        {
            if (field.Kind == FieldValueKind.Plain)
            {
                rows.Add(new FieldValueRecord
                {
                    Id = _ids.FieldValues.Take(path, values++),
                    EntityId = id,
                    UniverseId = universeId,
                    Field = field.Name,
                    FieldOrder = field.Order,
                    Kind = "plain",
                    Line = field.Line,
                    ValueJson = Json(field.Value),
                    OwnStatus = entity.Status.ToKey(),
                    Status = entity.Status.ToKey(),
                    IsLive = entity.Status.IsInCanon(),
                    FromOrdinal = long.MinValue,
                    UntilOrdinal = long.MaxValue,
                });
                continue;
            }

            foreach (var entry in field.Entries)
            {
                rows.Add(new FieldValueRecord
                {
                    Id = _ids.FieldValues.Take(path, values++),
                    EntityId = id,
                    UniverseId = universeId,
                    Field = field.Name,
                    FieldOrder = field.Order,
                    Kind = field.Kind == FieldValueKind.Entry ? "entry" : "temporal",
                    EntryIndex = entry.Index,
                    Line = entry.Line,
                    ValueJson = Json(entry.Value),
                    FromText = entry.FromText,
                    UntilText = entry.UntilText,
                    OwnStatus = entry.OwnStatus.ToKey(),
                    Status = entry.Status.ToKey(),
                    Pinned = entry.Pinned,
                    IsLive = entry.IsLive,
                    FromOrdinal = entry.EffectiveFrom?.Ordinal,
                    UntilOrdinal = entry.EffectiveUntil?.Ordinal,
                });
            }
        }

        foreach (var fact in facts.Sections)
        {
            var section = fact.Section;
            var sectionId = _ids.Sections.Take(path, section.Index);
            sectionIds[section] = sectionId;
            rows.Add(new SectionRecord
            {
                Id = sectionId,
                EntityId = id,
                UniverseId = universeId,
                ParentId = section.Parent is { } parent ? sectionIds[parent] : null,
                Index = section.Index,
                Level = section.Level,
                Name = section.Name,
                NameNormalized = LinkText.Normalize(section.Name),
                HeadingPathJson = JsonSerializer.Serialize(HeadingPath(section), JsonOptions),
                Line = section.Line,
                Body = section.Body,
                AttributesJson = section.Comment is { } comment ? Attributes(comment) : null,
                OwnStatus = fact.OwnStatus.ToKey(),
                Status = fact.Status.ToKey(),
                Pinned = fact.Pinned,
                IsBookkeeping = fact.IsBookkeeping,
                FromText = fact.FromText,
                UntilText = fact.UntilText,
                FromOrdinal = fact.EffectiveFrom?.Point.Ordinal,
                UntilOrdinal = fact.EffectiveUntil?.Ordinal,
            });
            search.Add(Search(path, section.Index, section.Name, section.Body, "section", id, sectionId));
        }

        return new EntityRows
        {
            Entity = entity,
            HasDuplicateId = hasDuplicateId,
            Id = id,
            Rows = rows,
            Search = search,
            SectionIds = sectionIds,
        };
    }

    /// <summary>
    /// A search row. HTML comments (F-SEC-11) are searchable, in a column of
    /// their own, so a hit can say it came from one.
    /// </summary>
    private SearchRow Search(string path, int key, string title, string body, string kind, int entityId, int? sectionId)
    {
        var (text, notes) = MarkdownComments.Split(body);
        return new SearchRow(_ids.Search.Take(path, key), title, text.TrimStart('\r', '\n'), notes, kind, entityId, sectionId);
    }

    private void WriteLinks(LinkIndex links)
    {
        var ordinal = 0;
        string? path = null;
        foreach (var link in links.Links)
        {
            var occurrence = link.Occurrence;
            var resolution = link.Resolution;
            if (occurrence.Path != path)
            {
                path = occurrence.Path;
                ordinal = 0;
            }

            var id = _ids.Links.Take(path, ordinal++);
            var outcome = Names<LinkOutcome>.Lower[link.Outcome];
            var matchedBy = resolution.MatchedBy is { } tier ? Names<LinkMatchKind>.Lower[tier] : null;
            var matches = link.Outcome == LinkOutcome.Ambiguous ? JsonSerializer.Serialize(resolution.Matches.Select(m => m.Key), JsonOptions) : null;

            // A link found in a file that didn't change is the same occurrence
            // object. Its row is the same unless what it resolves to changed.
            if (_previous?.LinkRows.GetValueOrDefault(occurrence) is { } kept
                && kept.Id == id && kept.Outcome == outcome && kept.MatchedBy == matchedBy && kept.TargetKey == link.Target?.Key
                && kept.TargetSection == link.TargetSection?.Name && kept.MatchesJson == matches && kept.Problem == resolution.Problem)
            {
                _linkRows[occurrence] = kept;
                _rows.Add(kept);
                continue;
            }

            var row = new LinkRecord
            {
                Id = id,
                FileId = _files[occurrence.Path],
                UniverseId = _containers[occurrence.Scope.Universe],
                Path = occurrence.Path,
                Line = occurrence.Link.Line,
                Column = occurrence.Link.Column,
                Text = occurrence.Link.Text,
                Target = occurrence.Link.Target,
                Heading = occurrence.Link.Heading,
                Label = occurrence.Link.Label,
                Site = Names<LinkSite>.Snake[occurrence.Site],
                ScopeId = _containers[occurrence.Scope],
                SourceEntityId = occurrence.Entity is { } source ? _entities[source] : null,
                SourceSectionId = occurrence.Section is { } section ? _sections[section] : null,
                Field = occurrence.Field,
                ChapterId = occurrence.Chapter is { } chapter ? _chapters[chapter] : null,
                OutlineRow = occurrence.OutlineRow,
                Status = occurrence.Status is { } status ? Names<FactStatus>.Lower[status] : null,
                IsBookkeeping = occurrence.IsBookkeeping,
                Outcome = outcome,
                MatchedBy = matchedBy,
                TargetKey = link.Target?.Key,
                TargetSection = link.TargetSection?.Name,
                MatchesJson = matches,
                Problem = resolution.Problem,
                PositionOrdinal = occurrence.Positions.Count > 0 ? occurrence.Positions.Min(p => p.Point.Ordinal) : null,
            };
            _linkRows[occurrence] = row;
            _rows.Add(row);
        }
    }

    /// <summary>An enum's values as the index stores them, worked out once rather than for every link.</summary>
    private static class Names<T>
        where T : struct, Enum
    {
        public static readonly Dictionary<T, string> Lower = Enum.GetValues<T>().ToDictionary(v => v, v => v.ToString().ToLowerInvariant());

        public static readonly Dictionary<T, string> Snake = Enum.GetValues<T>().ToDictionary(v => v, v => IndexDbContext.SnakeCase(v.ToString()));
    }

    /// <summary>
    /// The outlines of the books that loaded, read from <paramref name="source"/>,
    /// or for an update kept from <paramref name="previous"/> unless named in <paramref name="changed"/>.
    /// </summary>
    private static List<OutlineFile> ReadOutlines(
        DenStructure structure, IDenSource source, IReadOnlyList<OutlineFile>? previous, IReadOnlySet<string> changed)
    {
        var before = previous?.ToDictionary(o => o.Path, StringComparer.Ordinal);
        var result = new List<OutlineFile>();
        foreach (var book in structure.Universes.SelectMany(u => u.Books))
        {
            var path = $"{book.Path}/outline.md";
            if (before is not null && !changed.Contains(path))
            {
                if (before.TryGetValue(path, out var kept))
                {
                    result.Add(kept);
                }

                continue;
            }

            if (source.TryReadFile(path) is { } bytes)
            {
                var file = MarkdownFile.ParseOutline(bytes);
                result.Add(new OutlineFile(book, path, OutlineReader.IsExcluded(file) ? null : OutlineReader.ReadRows(file)));
            }
        }

        return result;
    }

    private void WriteOutlines(IReadOnlyList<OutlineFile> outlines)
    {
        foreach (var (book, path, rows) in outlines)
        {
            var ordinal = 0;
            foreach (var row in rows ?? [])
            {
                _rows.Add(new OutlineRowRecord
                {
                    Id = _ids.OutlineRows.Take(path, ordinal++),
                    FileId = _files[path],
                    BookId = _containers[book],
                    UniverseId = _containers[book.Universe],
                    RowId = row.RowId,
                    Number = row.Number,
                    Suffix = row.Suffix,
                    Order = row.Order,
                    Line = row.Line,
                    Heading = row.Heading,
                    Beat = row.Beat,
                    Chapter = row.Chapter,
                    ChapterOrdinal = row.Chapter is { } key ? book.FindChapter(key)?.Point.Ordinal : null,
                    Pov = row.Pov,
                    Status = row.Status.ToKey(),
                    Notes = row.Notes,
                    CellsJson = JsonSerializer.Serialize(row.Cells, JsonOptions),
                });
            }
        }
    }

    /// <summary>Diagnostics, by path and line, with ids 1, 2, 3 in that order, in a full build and an update alike.</summary>
    private void WriteDiagnostics(IEnumerable<DenDiagnostic> diagnostics)
    {
        var id = 0;
        foreach (var d in diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Diagnostic.Line ?? 0))
        {
            _rows.Add(new DiagnosticRecord
            {
                Id = ++id,
                Path = d.Path,
                RuleId = d.Diagnostic.RuleId,
                Severity = d.Diagnostic.Severity.ToString().ToLowerInvariant(),
                Line = d.Diagnostic.Line,
                Message = d.Diagnostic.Message,
            });
        }
    }

    /// <summary>
    /// The files of the enabled den-local packs that loaded: those without
    /// an error, which would exclude them (types.md F-PACK-2).
    /// </summary>
    private static List<string> LoadedPackFiles(DenStructure structure, IEnumerable<string> read)
    {
        var failed = structure.Types.Diagnostics
            .Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Path)
            .ToHashSet(StringComparer.Ordinal);
        var folders = structure.Types.Packs.Where(p => !p.IsBuiltIn).Select(p => p.Folder + "/").ToList();
        return read
            .Where(p => folders.Any(f => p.StartsWith(f, StringComparison.Ordinal)) && !failed.Contains(p))
            .ToList();
    }

    private static IEnumerable<Container> AllContainers(DenStructure structure) =>
        structure.Universes.SelectMany(u => new Container[] { u }.Concat(u.Series).Concat(u.Books));

    private static int IndexOf(Universe universe, Book book)
    {
        for (var i = 0; i < universe.Books.Count; i++)
        {
            if (universe.Books[i] == book)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Book '{book.Key}' isn't in universe '{universe.Key}'.");
    }

    internal static string FileKind(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return path == "den.yaml" ? "den"
            : path.StartsWith("packs/", StringComparison.Ordinal) ? "pack"
            : name is "universe.md" or "series.md" or "book.md" ? "container"
            : name == "outline.md" ? "outline"
            : path.Contains("/entities/", StringComparison.Ordinal) ? "entity"
            : "other";
    }

    /// <summary>F-DEN-2: <c>name</c> from <c>den.yaml</c>, or the den root's folder name.</summary>
    private string DenName(IDenSource source)
    {
        if (source.TryReadFile("den.yaml") is { } bytes
            && YamlFile.Parse(bytes).Root?["name"] is YamlScalar { IsNull: false } name
            && name.Text.Trim().Length > 0)
        {
            return name.Text;
        }

        return Path.GetFileName(Path.TrimEndingDirectorySeparator(_root));
    }

    private static List<string> HeadingPath(Section section)
    {
        var path = new List<string>();
        for (Section? s = section; s is not null; s = s.Parent)
        {
            path.Insert(0, s.Name);
        }

        return path;
    }

    private static string Attributes(SectionComment comment)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in comment.Attributes)
        {
            map.TryAdd(attribute.Name, attribute.Value);
        }

        return JsonSerializer.Serialize(map, JsonOptions);
    }

    /// <summary>A YAML value as JSON. Scalars stay strings as written, and nulls are null.</summary>
    internal static string Json(YamlNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JsonOptions.Encoder }))
        {
            Write(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());

        static void Write(Utf8JsonWriter writer, YamlNode? node)
        {
            switch (node)
            {
                case null or YamlScalar { IsNull: true }:
                    writer.WriteNullValue();
                    break;
                case YamlScalar scalar:
                    writer.WriteStringValue(scalar.Text);
                    break;
                case YamlSequence list:
                    writer.WriteStartArray();
                    foreach (var item in list.Items)
                    {
                        Write(writer, item);
                    }

                    writer.WriteEndArray();
                    break;
                case YamlMapping map:
                    writer.WriteStartObject();
                    foreach (var entry in map.Entries)
                    {
                        writer.WritePropertyName(entry.KeyText ?? "");
                        Write(writer, entry.Value);
                    }

                    writer.WriteEndObject();
                    break;
            }
        }
    }
}
