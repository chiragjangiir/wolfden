using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wolfden.Index;

/// <summary>A full-text search result: an entity, or one of its sections.</summary>
/// <param name="EntityKey">The entity's key: its id, or its path while it has none.</param>
/// <param name="EntityName">The entity's name.</param>
/// <param name="EntityPath">The entity file's path.</param>
/// <param name="SectionName">The section's name, or null if the match is the entity's name, aliases, or lead.</param>
/// <param name="SectionLine">The section heading's line, or null.</param>
/// <param name="Snippet">A short excerpt with each match wrapped in <c>[</c> and <c>]</c>.</param>
/// <param name="Rank">The BM25 score; lower is better.</param>
public sealed record SearchHit(string EntityKey, string EntityName, string EntityPath, string? SectionName, int? SectionLine, string Snippet, double Rank);

/// <summary>
/// Read access to a den's index (design.md §7). Opening it makes sure the
/// index is current first (<see cref="IndexBuilder.EnsureCurrent"/>), so a
/// missing, corrupt, or outdated index is rebuilt rather than reported.
/// The connection is read-only, and it's open only while a query runs, so
/// a rebuild can swap in a new index file between queries, even on Windows,
/// where an open file can't be replaced. Later queries read the new file.
/// </summary>
public sealed class DenIndex : IDisposable
{
    private readonly SqliteConnection _connection;

    private DenIndex(SqliteConnection connection)
    {
        _connection = connection;
        Context = new IndexDbContext(connection);
        Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>The EF Core context, for queries beyond this API.</summary>
    public IndexDbContext Context { get; }

    /// <summary>
    /// Opens the index of the den at <paramref name="denRoot"/>, rebuilding
    /// it first if it isn't current.
    /// </summary>
    public static DenIndex Open(string denRoot)
    {
        IndexBuilder.EnsureCurrent(denRoot);
        // Left closed: EF Core opens and closes a closed connection around each query.
        var connection = new SqliteConnection(IndexBuilder.ConnectionString(IndexBuilder.IndexPath(denRoot), SqliteOpenMode.ReadOnly));
        return new DenIndex(connection);
    }

    /// <summary>
    /// The entity with this id (the <c>id</c> in its frontmatter), key, or
    /// path, or null. Where copies share an id (F-ID-3), the first by path.
    /// </summary>
    public EntityRecord? FindEntity(string idKeyOrPath)
    {
        ArgumentNullException.ThrowIfNull(idKeyOrPath);
        return Context.Entities
            .Where(e => e.Ulid == idKeyOrPath || e.Key == idKeyOrPath || e.Path == idKeyOrPath)
            .OrderBy(e => e.Path)
            .FirstOrDefault();
    }

    /// <summary>The links that resolve to the entity with this key: its backlinks, by path and place in the file.</summary>
    public IReadOnlyList<LinkRecord> Backlinks(string entityKey)
    {
        ArgumentNullException.ThrowIfNull(entityKey);
        return Context.Links
            .Where(l => l.TargetKey == entityKey)
            .OrderBy(l => l.Path).ThenBy(l => l.Line).ThenBy(l => l.Column)
            .ToList();
    }

    /// <summary>
    /// Full-text search for every word of <paramref name="text"/>, in any
    /// order, ignoring case and accents. Words are matched whole; a word
    /// ending in <c>*</c> matches as a prefix (<c>ambu*</c>). Other FTS5
    /// syntax is searched for as plain text.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string text, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Term)
            .Where(t => t.Length > 0)
            .ToList();
        return terms.Count == 0 ? [] : SearchMatch(string.Join(' ', terms), limit);

        static string Term(string word)
        {
            var prefix = word.EndsWith('*');
            var bare = word.TrimEnd('*');
            return bare.Length == 0 ? "" : $"\"{bare.Replace("\"", "\"\"", StringComparison.Ordinal)}\"{(prefix ? "*" : "")}";
        }
    }

    /// <summary>
    /// Full-text search with an FTS5 query expression, such as
    /// <c>title:pike OR "long watch"</c>. Names and aliases are the
    /// <c>title</c> column, and leads and section text the <c>body</c>.
    /// Matches in a title rank higher.
    /// </summary>
    /// <exception cref="SqliteException">The expression isn't valid FTS5 syntax.</exception>
    public IReadOnlyList<SearchHit> SearchMatch(string fts5Query, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(fts5Query);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.key, e.name, e.path, s.name, s.line,
                   snippet(search, -1, '[', ']', '…', 12), bm25(search, 10.0, 1.0) AS rank
            FROM search
            JOIN entities e ON e.id = search.entity_id
            LEFT JOIN sections s ON s.id = search.section_id
            WHERE search MATCH $query
            ORDER BY rank, search.rowid
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", fts5Query);
        command.Parameters.AddWithValue("$limit", limit);
        var hits = new List<SearchHit>();
        _connection.Open();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new SearchHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetDouble(6)));
            }
        }
        finally
        {
            _connection.Close();
        }

        return hits;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
