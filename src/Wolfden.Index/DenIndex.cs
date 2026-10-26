using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wolfden.Core.Links;

namespace Wolfden.Index;

/// <summary>
/// A full-text search result: an entity, or one of its sections. Search
/// finds out-of-canon and bookkeeping material too, so callers that want
/// canon only (such as an AI agent's context) filter on <see cref="IsInCanon"/>.
/// </summary>
/// <param name="EntityKey">The entity's key: its id, or its path while it has none.</param>
/// <param name="EntityName">The entity's name.</param>
/// <param name="EntityPath">The entity file's path.</param>
/// <param name="SectionName">The section's name, or null if the match is the entity's name, aliases, or lead.</param>
/// <param name="SectionLine">The section heading's line, or null.</param>
/// <param name="Snippet">
/// A short excerpt with each match wrapped in <c>[</c> and <c>]</c>, for
/// reading. The note's own text can hold brackets too (<c>[[a link]]</c>);
/// <see cref="Text"/> and <see cref="Matches"/> tell them apart.
/// </param>
/// <param name="Rank">The BM25 score; lower is better.</param>
/// <param name="Status">The effective status (F-STAT-3) of the section, or of the entity for its name, aliases, and lead.</param>
/// <param name="IsBookkeeping">True for a match in a bookkeeping section (F-SEC-12).</param>
/// <param name="InComment">True if a match is in an HTML comment (F-SEC-11), such as a to-do note, rather than in the text.</param>
/// <param name="Text">The excerpt without marks.</param>
/// <param name="Matches">Where the matches are in <see cref="Text"/>, in order.</param>
public sealed record SearchHit(
    string EntityKey, string EntityName, string EntityPath, string? SectionName, int? SectionLine, string Snippet, double Rank,
    string Status, bool IsBookkeeping, bool InComment, string Text, IReadOnlyList<TextMatch> Matches)
{
    /// <summary>True if the hit is canon text: its status is <c>draft</c> or <c>canon</c>, and it isn't bookkeeping.</summary>
    public bool IsInCanon => Status is "draft" or "canon" && !IsBookkeeping;
}

/// <summary>
/// Read access to a den's index (design.md §7). Opening it rebuilds an
/// index that's missing, corrupt, or from another schema version or den
/// format (<see cref="IndexBuilder.EnsureCurrent"/>). It doesn't notice
/// edits to the den's files since the last build: keeping the index up to
/// date with the files is the job of <see cref="DenWatcher"/> (#14), or of
/// <see cref="IndexBuilder.EnsureUpToDate(string)"/> for a one-off command.
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
    /// it first if it's missing, corrupt, or from another schema version or
    /// den format. An existing index isn't checked against the files.
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

    /// <summary>
    /// Every entity of the universe with key <paramref name="universeKey"/>,
    /// whatever its status or where it enters canon, in the order views list
    /// them (F-VIEW-3). With a <paramref name="filter"/>, only the entities
    /// filed in the scopes a view with that filter covers (F-VIEW-1).
    /// </summary>
    /// <exception cref="CanonViewException">The universe or filter doesn't exist.</exception>
    public IReadOnlyList<EntityRecord> Entities(string universeKey, CanonFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(universeKey);
        return CanonQuery.Entities(Context, universeKey, filter);
    }

    /// <summary>
    /// For each id used by an entity outside the universe with key
    /// <paramref name="universeKey"/>, the paths of those entity files, in
    /// one query: what a check of that universe needs to find ids shared
    /// across universes (F-ID-3) without reading the other universes' notes.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> IdsOutside(string universeKey)
    {
        ArgumentNullException.ThrowIfNull(universeKey);
        var rows = (
            from e in Context.Entities
            join u in Context.Containers on e.UniverseId equals u.Id
            where e.Ulid != null && u.Key != universeKey
            select new { e.Ulid, e.Path }).ToList();
        return rows
            .GroupBy(r => r.Ulid!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Path).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The entities of the universe with row id <paramref name="universeId"/>
    /// that <paramref name="name"/> names, matched as a link is
    /// (<see cref="LinkMatching"/>, F-LINK-4, F-LINK-6): every match in the
    /// first tier that has any (id, then file name, then name, then alias),
    /// in path order. With no file to resolve from, there's no scope chain to
    /// break a tie (F-LINK-7), so a tie keeps all its matches.
    /// </summary>
    /// <param name="universeId">The universe's row id.</param>
    /// <param name="name">The name, as a link would give it.</param>
    /// <param name="amongPaths">
    /// If given, only the entities with these paths take part, so the tiers
    /// run over them alone: for a canon view, the entities the reader has met,
    /// so an entity met later can't shadow or tie with one met already.
    /// </param>
    public IReadOnlyList<EntityRecord> MatchEntities(int universeId, string name, IReadOnlySet<string>? amongPaths = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var entities = Context.Entities.Where(e => e.UniverseId == universeId).ToList()
            .Where(e => amongPaths is null || amongPaths.Contains(e.Path))
            .ToList();
        var ids = entities.Select(e => e.Id).ToList();
        var aliases = Context.Aliases.Where(a => ids.Contains(a.EntityId)).Select(a => new { a.EntityId, a.AliasNormalized }).ToList();
        var byId = entities.ToDictionary(e => e.Id);
        var (_, matches) = LinkMatching.FirstTier<EntityRecord>(
            name,
            id => entities.Where(e => e.Ulid == id).ToList(),
            slug => entities.Where(e => e.Slug is not null && LinkText.Normalize(e.Slug) == slug).ToList(),
            normalized => entities.Where(e => e.NameNormalized == normalized).ToList(),
            normalized => aliases.Where(a => a.AliasNormalized == normalized).Select(a => a.EntityId).Distinct().Select(id => byId[id]).ToList());
        return matches.DistinctBy(e => e.Id).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The canon-as-of view (format.md F-VIEW-1) of the universe with key
    /// <paramref name="universeKey"/> at <paramref name="position"/>: a
    /// <c>book:chapter</c> or bare book position, or null for the end of the
    /// story (all canon so far). It covers entities at every scope of the
    /// universe, or with a <paramref name="filter"/> only those filed in that
    /// series or book, the scopes enclosing it, and the scopes inside it.
    /// The position can be anywhere in the universe, inside the filter or not.
    /// </summary>
    /// <exception cref="CanonViewException">The universe or filter doesn't exist, or the position doesn't parse or resolve.</exception>
    public CanonView CanonAsOf(string universeKey, string? position = null, CanonFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(universeKey);
        return CanonQuery.Run(Context, universeKey, position, filter);
    }

    /// <summary>
    /// The place of <paramref name="position"/> in the story order of the
    /// universe with key <paramref name="universeKey"/> (F-ORD-2, F-ORD-3),
    /// as the index's ordinals give it, or the end of the story for null:
    /// the point a canon view at that position is taken at.
    /// </summary>
    /// <exception cref="CanonViewException">The universe doesn't exist, or the position doesn't parse or resolve.</exception>
    public long PointOf(string universeKey, string? position)
    {
        ArgumentNullException.ThrowIfNull(universeKey);
        return CanonQuery.PointOf(Context, universeKey, position);
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
    /// syntax is searched for as plain text, and control characters are
    /// treated as spaces, so no input throws. It finds every status,
    /// bookkeeping, and HTML comments; see <see cref="SearchHit.IsInCanon"/>.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string text, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(text);

        // FTS5 reads a query as a C string, so a NUL would end it early.
        var clean = string.Create(text.Length, text, (span, t) =>
        {
            for (var i = 0; i < t.Length; i++)
            {
                span[i] = char.IsControl(t[i]) ? ' ' : t[i];
            }
        });
        var terms = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
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
    /// <c>title</c> column, leads and section text the <c>body</c>, and
    /// HTML comments in them <c>comments</c>. Matches in a title rank
    /// higher, and matches in comments lower.
    /// </summary>
    /// <exception cref="SqliteException">The expression isn't valid FTS5 syntax.</exception>
    public IReadOnlyList<SearchHit> SearchMatch(string fts5Query, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(fts5Query);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.key, e.name, e.path, s.name, s.line,
                   snippet(search, -1, char(1), char(2), '…', 12), bm25(search, 10.0, 1.0, 0.5) AS rank,
                   coalesce(s.status, e.status), coalesce(s.is_bookkeeping, 0),
                   instr(highlight(search, 2, char(1), char(2)), char(1)) > 0
            FROM search
            JOIN entities e ON e.id = search.entity_id
            LEFT JOIN sections s ON s.id = search.section_id
            WHERE search MATCH $query
            ORDER BY rank, e.path, coalesce(s."index", -1)
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
                // The search table holds no control characters (IndexWriter), so these marks are the snippet's own.
                var marked = MarkedText.FromMarks(reader.GetString(5), MarkedText.Open, MarkedText.Close);
                hits.Add(new SearchHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    marked.Bracketed(),
                    reader.GetDouble(6),
                    reader.GetString(7),
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    marked.Text,
                    marked.Matches));
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
