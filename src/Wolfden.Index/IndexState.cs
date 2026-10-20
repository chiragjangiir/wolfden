using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Outlines;
using Wolfden.Core.Structure;

namespace Wolfden.Index;

/// <summary>
/// Everything an index was written from, kept in memory by a file watcher
/// (#14) so the next change can be applied without loading the whole den
/// again: what Core loaded, the rows written, how row ids were given out,
/// and the den's tree as the files fingerprint sees it.
/// </summary>
internal sealed class IndexState
{
    /// <summary>The den root's full path.</summary>
    public required string Root { get; init; }

    /// <summary>The den's structure. An incremental update needs it unchanged.</summary>
    public required DenStructure Structure { get; init; }

    /// <summary>The entities.</summary>
    public required EntityCatalog Catalog { get; init; }

    /// <summary>The links.</summary>
    public required LinkIndex Links { get; init; }

    /// <summary>The outlines of the books that loaded, by path.</summary>
    public required IReadOnlyList<OutlineFile> Outlines { get; init; }

    /// <summary>The SHA-256 of every file read, by path: the <c>files</c> table.</summary>
    public required IReadOnlyDictionary<string, string> FileHashes { get; init; }

    /// <summary>The rows written.</summary>
    public required IndexRows Rows { get; init; }

    /// <summary>How row ids were given out, so the next update keeps them where it can.</summary>
    public required IndexIds Ids { get; init; }

    /// <summary>Each entity's rows, by path, kept while the entity doesn't change.</summary>
    public required IReadOnlyDictionary<string, EntityRows> Entities { get; init; }

    /// <summary>Each link's row, by the occurrence it came from, kept while the link's file doesn't change and it resolves the same.</summary>
    public required IReadOnlyDictionary<LinkOccurrence, LinkRecord> LinkRows { get; init; }

    /// <summary>The container rows' ids.</summary>
    public required IReadOnlyDictionary<Container, int> ContainerIds { get; init; }

    /// <summary>The chapter rows' ids.</summary>
    public required IReadOnlyDictionary<Chapter, int> ChapterIds { get; init; }

    /// <summary>The den's tree, for the files fingerprint.</summary>
    public required DenFileTree Tree { get; init; }

    /// <summary>The <see cref="IndexSchema.WriteIdKey"/> this state was written with.</summary>
    public required string WriteId { get; init; }
}

/// <summary>An <c>outline.md</c> of a book that loaded, and its rows, or null if the outline is excluded.</summary>
internal sealed record OutlineFile(Book Book, string Path, IReadOnlyList<OutlineRow>? Rows);

/// <summary>
/// The rows that come from one entity: its entity row, aliases, field
/// values, sections, and search rows, kept while the entity is the same
/// object (its file didn't change) and its duplicate-id flag is the same.
/// </summary>
internal sealed class EntityRows
{
    public required CatalogEntity Entity { get; init; }

    public required bool HasDuplicateId { get; init; }

    public required int Id { get; init; }

    public required IReadOnlyList<object> Rows { get; init; }

    public required IReadOnlyList<SearchRow> Search { get; init; }

    public required IReadOnlyDictionary<Section, int> SectionIds { get; init; }
}

/// <summary>A row of the FTS5 <c>search</c> table, with its rowid.</summary>
internal sealed record SearchRow(long RowId, string Title, string Body, string Comments, string Kind, int EntityId, int? SectionId);
