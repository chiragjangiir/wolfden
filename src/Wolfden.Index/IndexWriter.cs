using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Outlines;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Index;

/// <summary>
/// Loads a den with Core and turns what Core computes into index rows. Ids
/// are assigned in a fixed order (universes, then series and books in story
/// order; entities, links, and diagnostics by path), so building the same
/// files twice gives identical rows.
/// </summary>
internal sealed class IndexWriter
{
    // Keep non-ASCII text readable in the database ("Zoë", not "Zoë").
    // The JSON never goes into HTML, so the relaxed encoder is safe here.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _root;
    private readonly HashingDenSource _source;
    private readonly Dictionary<string, int> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<Container, int> _containers = [];
    private readonly Dictionary<Chapter, int> _chapters = [];
    private readonly Dictionary<CatalogEntity, int> _entities = [];
    private readonly Dictionary<Section, int> _sections = [];
    private readonly List<(string Title, string Body, string Kind, int EntityId, int? SectionId)> _search = [];

    private IndexWriter(string root)
    {
        _root = root;
        _source = new HashingDenSource(new FileSystemDenSource(root));
    }

    /// <summary>Loads the den at <paramref name="root"/> and writes its rows into an empty database.</summary>
    public static void Write(string root, IndexDbContext db, SqliteConnection connection)
    {
        new IndexWriter(root).Run(db, connection);
    }

    private void Run(IndexDbContext db, SqliteConnection connection)
    {
        var structure = DenStructure.Load(_source);
        var catalog = EntityCatalog.Load(structure, _source);
        var links = LinkIndex.Build(catalog, _source);
        var outlines = ReadOutlines(structure);

        var loaded = new HashSet<string>(StringComparer.Ordinal);
        if (structure.IsLoaded)
        {
            loaded.Add("den.yaml");
        }

        loaded.UnionWith(AllContainers(structure).Select(c => c.FilePath));
        loaded.UnionWith(catalog.Entities.Select(e => e.Path));
        loaded.UnionWith(outlines.Where(o => o.Rows is not null).Select(o => o.Path));
        foreach (var (path, hash) in _source.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var id = _files.Count + 1;
            _files[path] = id;
            db.Files.Add(new FileRecord { Id = id, Path = path, Kind = FileKind(path), Hash = hash, IsLoaded = loaded.Contains(path) });
        }

        db.Meta.AddRange(
            new MetaRecord { Key = IndexSchema.SchemaVersionKey, Value = IndexSchema.Version.ToString(CultureInfo.InvariantCulture) },
            new MetaRecord { Key = IndexSchema.DenFormatKey, Value = IndexBuilder.DenFormatStamp(_source.FileExists("den.yaml") ? _source.ReadFile("den.yaml") : null) },
            new MetaRecord { Key = IndexSchema.SupportedFormatKey, Value = DenStructure.SupportedFormat.ToString(CultureInfo.InvariantCulture) });
        db.Dens.Add(new DenRecord
        {
            Id = 1,
            Name = DenName(),
            Format = structure.Format,
            IsLoaded = structure.IsLoaded,
            SpoilerChecks = structure.SpoilerChecks,
            BookkeepingSectionsJson = JsonSerializer.Serialize(structure.BookkeepingSections, JsonOptions),
        });

        WriteContainers(db, structure);
        WriteEntities(db, catalog, new Bookkeeping(structure.BookkeepingSections));
        WriteLinks(db, links);
        WriteOutlines(db, outlines);
        WriteDiagnostics(db, structure.Diagnostics.Concat(catalog.Diagnostics).Concat(links.Diagnostics));

        db.SaveChanges();
        WriteSearch(connection);
    }

    private void WriteContainers(IndexDbContext db, DenStructure structure)
    {
        foreach (var universe in structure.Universes)
        {
            var universeId = AddContainer(db, universe, universe, null, null);
            foreach (var work in universe.Works)
            {
                if (work is Series series)
                {
                    var firstBook = series.Books.Count > 0 ? IndexOf(universe, series.Books[0]) : (int?)null;
                    var seriesId = AddContainer(db, series, universe, universeId, firstBook);
                    foreach (var book in series.Books)
                    {
                        AddBook(db, book, universe, universeId, seriesId);
                    }
                }
                else if (work is Book book)
                {
                    AddBook(db, book, universe, universeId, universeId);
                }
            }
        }
    }

    private void AddBook(IndexDbContext db, Book book, Universe universe, int universeId, int parentId)
    {
        var bookId = AddContainer(db, book, universe, parentId, IndexOf(universe, book));
        foreach (var chapter in book.Chapters)
        {
            var id = _chapters.Count + 1;
            _chapters[chapter] = id;
            db.Chapters.Add(new ChapterRecord
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

    private int AddContainer(IndexDbContext db, Container container, Universe universe, int? parentId, int? storyOrder)
    {
        var id = _containers.Count + 1;
        _containers[container] = id;
        db.Containers.Add(new ContainerRecord
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

    private void WriteEntities(IndexDbContext db, EntityCatalog catalog, Bookkeeping bookkeeping)
    {
        var aliasId = 0;
        var valueId = 0;
        foreach (var entity in catalog.Entities)
        {
            var id = _entities.Count + 1;
            _entities[entity] = id;
            var universeId = _containers[entity.Universe];
            var lead = entity.File.Lead;
            db.Entities.Add(new EntityRecord
            {
                Id = id,
                FileId = _files[entity.Path],
                UniverseId = universeId,
                ScopeId = _containers[entity.Scope],
                Ulid = entity.Id,
                Key = entity.Key,
                Path = entity.Path,
                Slug = entity.Slug,
                Name = entity.Name,
                NameNormalized = LinkText.Normalize(entity.Name),
                Type = entity.Type,
                Status = entity.Status.ToKey(),
                FromText = entity.From?.ToString(),
                FromOrdinal = entity.EntersCanon?.Ordinal,
                HasDuplicateId = entity.HasDuplicateId,
                Title = entity.File.Title,
                Lead = lead,
            });
            _search.Add((string.Join("\n", entity.Aliases.Prepend(entity.Name)), lead, "entity", id, null));

            for (var i = 0; i < entity.Aliases.Count; i++)
            {
                db.Aliases.Add(new AliasRecord
                {
                    Id = ++aliasId,
                    EntityId = id,
                    Order = i,
                    Alias = entity.Aliases[i],
                    AliasNormalized = LinkText.Normalize(entity.Aliases[i]),
                });
            }

            var facts = EntityFacts.For(entity, bookkeeping);
            foreach (var field in facts.Fields)
            {
                if (field.Kind == FieldValueKind.Plain)
                {
                    db.FieldValues.Add(new FieldValueRecord
                    {
                        Id = ++valueId,
                        EntityId = id,
                        UniverseId = universeId,
                        Field = field.Name,
                        FieldOrder = field.Order,
                        Kind = "plain",
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
                    db.FieldValues.Add(new FieldValueRecord
                    {
                        Id = ++valueId,
                        EntityId = id,
                        UniverseId = universeId,
                        Field = field.Name,
                        FieldOrder = field.Order,
                        Kind = field.Kind == FieldValueKind.Entry ? "entry" : "temporal",
                        EntryIndex = entry.Index,
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
                var sectionId = _sections.Count + 1;
                _sections[section] = sectionId;
                db.Sections.Add(new SectionRecord
                {
                    Id = sectionId,
                    EntityId = id,
                    UniverseId = universeId,
                    ParentId = section.Parent is { } parent ? _sections[parent] : null,
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
                _search.Add((section.Name, section.Body, "section", id, sectionId));
            }
        }
    }

    private void WriteLinks(IndexDbContext db, LinkIndex links)
    {
        var id = 0;
        foreach (var link in links.Links)
        {
            var occurrence = link.Occurrence;
            var resolution = link.Resolution;
            db.Links.Add(new LinkRecord
            {
                Id = ++id,
                FileId = _files[occurrence.Path],
                UniverseId = _containers[occurrence.Scope.Universe],
                Path = occurrence.Path,
                Line = occurrence.Link.Line,
                Column = occurrence.Link.Column,
                Text = occurrence.Link.Text,
                Target = occurrence.Link.Target,
                Heading = occurrence.Link.Heading,
                Label = occurrence.Link.Label,
                Site = IndexDbContext.SnakeCase(occurrence.Site.ToString()),
                ScopeId = _containers[occurrence.Scope],
                SourceEntityId = occurrence.Entity is { } source ? _entities[source] : null,
                SourceSectionId = occurrence.Section is { } section ? _sections[section] : null,
                Field = occurrence.Field,
                ChapterId = occurrence.Chapter is { } chapter ? _chapters[chapter] : null,
                OutlineRow = occurrence.OutlineRow,
                Status = occurrence.Status?.ToKey(),
                IsBookkeeping = occurrence.IsBookkeeping,
                Outcome = link.Outcome.ToString().ToLowerInvariant(),
                MatchedBy = resolution.MatchedBy?.ToString().ToLowerInvariant(),
                TargetKey = link.Target?.Key,
                TargetSection = link.TargetSection?.Name,
                MatchesJson = link.Outcome == LinkOutcome.Ambiguous ? JsonSerializer.Serialize(resolution.Matches.Select(m => m.Key), JsonOptions) : null,
                Problem = resolution.Problem,
                PositionOrdinal = occurrence.Positions.Count > 0 ? occurrence.Positions.Min(p => p.Point.Ordinal) : null,
            });
        }
    }

    private List<(Book Book, string Path, IReadOnlyList<OutlineRow>? Rows)> ReadOutlines(DenStructure structure)
    {
        var result = new List<(Book, string, IReadOnlyList<OutlineRow>?)>();
        foreach (var book in structure.Universes.SelectMany(u => u.Books))
        {
            var path = $"{book.Path}/outline.md";
            if (_source.FileExists(path))
            {
                var file = MarkdownFile.Parse(_source.ReadFile(path));
                result.Add((book, path, OutlineReader.IsExcluded(file) ? null : OutlineReader.ReadRows(file)));
            }
        }

        return result;
    }

    private void WriteOutlines(IndexDbContext db, List<(Book Book, string Path, IReadOnlyList<OutlineRow>? Rows)> outlines)
    {
        var id = 0;
        foreach (var (book, path, rows) in outlines)
        {
            foreach (var row in rows ?? [])
            {
                db.OutlineRows.Add(new OutlineRowRecord
                {
                    Id = ++id,
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

    private static void WriteDiagnostics(IndexDbContext db, IEnumerable<DenDiagnostic> diagnostics)
    {
        var id = 0;
        foreach (var d in diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Diagnostic.Line ?? 0))
        {
            db.Diagnostics.Add(new DiagnosticRecord
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

    private void WriteSearch(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = IndexSchema.InsertSearchRow;
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var body = command.Parameters.Add("$body", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var entity = command.Parameters.Add("$entity", SqliteType.Integer);
        var section = command.Parameters.Add("$section", SqliteType.Integer);
        foreach (var row in _search)
        {
            title.Value = row.Title;
            body.Value = row.Body;
            kind.Value = row.Kind;
            entity.Value = row.EntityId;
            section.Value = (object?)row.SectionId ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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

    private static string FileKind(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return path == "den.yaml" ? "den"
            : name is "universe.md" or "series.md" or "book.md" ? "container"
            : name == "outline.md" ? "outline"
            : path.Contains("/entities/", StringComparison.Ordinal) ? "entity"
            : "other";
    }

    /// <summary>F-DEN-2: <c>name</c> from <c>den.yaml</c>, or the den root's folder name.</summary>
    private string DenName()
    {
        if (_source.FileExists("den.yaml")
            && YamlFile.Parse(_source.ReadFile("den.yaml")).Root?["name"] is YamlScalar { IsNull: false } name
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
