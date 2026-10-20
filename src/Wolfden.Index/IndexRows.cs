using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wolfden.Index;

/// <summary>
/// Every row of an index, in memory: what a full build inserts, and what an
/// incremental update (#14) compares with the rows before to write only the
/// difference.
/// </summary>
internal sealed class IndexRows
{
    /// <summary>The model's tables in an order that respects foreign keys: a row's parents come first.</summary>
    internal static readonly Type[] TableOrder =
    [
        typeof(MetaRecord), typeof(DenRecord), typeof(FileRecord), typeof(ContainerRecord), typeof(ChapterRecord),
        typeof(FieldDefaultRecord), typeof(EntityRecord), typeof(AliasRecord), typeof(FieldValueRecord), typeof(SectionRecord),
        typeof(LinkRecord), typeof(OutlineRowRecord), typeof(DiagnosticRecord),
    ];

    private readonly Dictionary<Type, Dictionary<int, object>> _tables = [];

    public IndexRows()
    {
        foreach (var type in TableOrder.Where(t => t != typeof(MetaRecord)))
        {
            _tables[type] = [];
        }
    }

    /// <summary>The <c>meta</c> rows, by key.</summary>
    public Dictionary<string, string> Meta { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>search</c> rows, by rowid.</summary>
    public Dictionary<long, SearchRow> Search { get; private set; } = [];

    /// <summary>
    /// The same rows as <paramref name="other"/>, sharing its tables (rows
    /// aren't changed once built), with a copy of its <c>meta</c> to change.
    /// </summary>
    public static IndexRows SameAs(IndexRows other)
    {
        var rows = new IndexRows { Search = other.Search };
        foreach (var type in other._tables.Keys)
        {
            rows._tables[type] = other._tables[type];
        }

        foreach (var (key, value) in other.Meta)
        {
            rows.Meta[key] = value;
        }

        return rows;
    }

    /// <summary>The number of rows in every table, <c>meta</c> and <c>search</c> included.</summary>
    public int Count => Meta.Count + Search.Count + _tables.Values.Sum(t => t.Count);

    /// <summary>The rows of one table, by id.</summary>
    public IReadOnlyDictionary<int, object> Table(Type type) => _tables[type];

    /// <summary>Adds a row of a table with an <c>Id</c>.</summary>
    public void Add(object row) => _tables[row.GetType()].Add(
        row switch
        {
            LinkRecord r => r.Id,
            SectionRecord r => r.Id,
            FieldValueRecord r => r.Id,
            AliasRecord r => r.Id,
            EntityRecord r => r.Id,
            FileRecord r => r.Id,
            OutlineRowRecord r => r.Id,
            DiagnosticRecord r => r.Id,
            _ => Columns.For(row.GetType()).Id(row),
        },
        row);

    /// <summary>Shares a whole table with <paramref name="other"/>, for tables an update doesn't touch.</summary>
    public void ShareTable(Type type, IndexRows other) => _tables[type] = other._tables[type];

    /// <summary>
    /// Inserts every row into an empty index, table by table and each table
    /// in id order, then the search rows, in one transaction. Each table
    /// gets one prepared INSERT built from the EF model, which is far faster
    /// than EF's change tracker for a whole den.
    /// </summary>
    public void Insert(IndexDbContext db, SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value)";
            var key = meta.Parameters.Add("$key", SqliteType.Text);
            var value = meta.Parameters.Add("$value", SqliteType.Text);
            foreach (var (k, v) in Meta)
            {
                key.Value = k;
                value.Value = v;
                meta.ExecuteNonQuery();
            }
        }

        foreach (var type in TableOrder.Where(t => t != typeof(MetaRecord)))
        {
            using var insert = Statements.Insert(db, connection, transaction, type);
            foreach (var (_, row) in _tables[type].OrderBy(r => r.Key))
            {
                insert.Run(row);
            }
        }

        using (var search = new SearchStatements(connection, transaction))
        {
            foreach (var row in Search.Values.OrderBy(r => r.RowId))
            {
                search.Insert(row);
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Writes the difference between <paramref name="before"/> (what the
    /// index holds) and these rows, inside <paramref name="transaction"/>:
    /// rows that are gone are deleted, new rows inserted, and rows whose
    /// columns changed updated. Returns the number of rows written.
    /// </summary>
    public int ApplyChanges(IndexRows before, IndexDbContext db, SqliteConnection connection, SqliteTransaction transaction)
    {
        var written = 0;

        // Foreign keys are checked at the commit, so the order of the
        // statements doesn't matter to them. Deletes still go children first,
        // so no cascade removes a row that's about to be updated.
        Execute(connection, transaction, "PRAGMA defer_foreign_keys = ON");
        var changes = new Dictionary<Type, (List<object> Inserts, List<object> Updates, List<int> Deletes)>();
        foreach (var type in TableOrder.Where(t => t != typeof(MetaRecord)))
        {
            var old = before._tables[type];
            var now = _tables[type];
            if (ReferenceEquals(old, now))
            {
                continue;
            }

            var columns = Columns.For(type);
            var inserts = new List<object>();
            var updates = new List<object>();
            foreach (var (id, row) in now)
            {
                if (!old.TryGetValue(id, out var was))
                {
                    inserts.Add(row);
                }
                else if (!ReferenceEquals(was, row) && !columns.Same(was, row))
                {
                    updates.Add(row);
                }
            }

            var deletes = old.Keys.Where(id => !now.ContainsKey(id)).ToList();
            changes[type] = (inserts, updates, deletes);
        }

        foreach (var type in TableOrder.Reverse())
        {
            if (changes.TryGetValue(type, out var c) && c.Deletes.Count > 0)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM \"{db.Model.FindEntityType(type)!.GetTableName()}\" WHERE id = $id";
                var id = delete.Parameters.Add("$id", SqliteType.Integer);
                delete.Prepare();
                foreach (var d in c.Deletes)
                {
                    id.Value = d;
                    written += delete.ExecuteNonQuery();
                }
            }
        }

        foreach (var type in TableOrder)
        {
            if (!changes.TryGetValue(type, out var c))
            {
                continue;
            }

            if (c.Inserts.Count > 0)
            {
                using var insert = Statements.Insert(db, connection, transaction, type);
                foreach (var row in c.Inserts)
                {
                    written += insert.Run(row);
                }
            }

            if (c.Updates.Count > 0)
            {
                using var update = Statements.Update(db, connection, transaction, type);
                foreach (var row in c.Updates)
                {
                    written += update.Run(row);
                }
            }
        }

        using (var search = new SearchStatements(connection, transaction))
        {
            foreach (var (rowId, _) in ReferenceEquals(before.Search, Search) ? [] : before.Search)
            {
                if (!Search.TryGetValue(rowId, out var row) || row != before.Search[rowId])
                {
                    written += search.Delete(rowId);
                }
            }

            foreach (var (rowId, row) in Search)
            {
                if (!before.Search.TryGetValue(rowId, out var was) || was != row)
                {
                    written += search.Insert(row);
                }
            }
        }

        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            var key = meta.Parameters.Add("$key", SqliteType.Text);
            var value = meta.Parameters.Add("$value", SqliteType.Text);
            foreach (var (k, v) in Meta)
            {
                if (before.Meta.GetValueOrDefault(k) != v)
                {
                    key.Value = k;
                    value.Value = v;
                    written += meta.ExecuteNonQuery();
                }
            }
        }

        return written;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The mapped properties of one model type, with compiled getters, which are much faster than reflection for a whole den.</summary>
    private sealed class Columns
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Columns> Cache = new();

        private readonly Func<object, object?>[] _getters;
        private readonly Func<object, object?> _id;

        private Columns(Type type)
        {
            Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite).ToArray();
            _getters = Properties.Select(Getter).ToArray();
            _id = Properties.FirstOrDefault(p => p.Name == "Id") is { } id ? Getter(id) : _ => null;
        }

        public PropertyInfo[] Properties { get; }

        public static Columns For(Type type) => Cache.GetOrAdd(type, t => new Columns(t));

        public int Id(object row) => (int)_id(row)!;

        public Func<object, object?> GetterOf(PropertyInfo property) => _getters[Array.IndexOf(Properties, property)];

        public bool Same(object a, object b)
        {
            foreach (var get in _getters)
            {
                if (!Equals(get(a), get(b)))
                {
                    return false;
                }
            }

            return true;
        }

        private static Func<object, object?> Getter(PropertyInfo property)
        {
            var row = Expression.Parameter(typeof(object), "row");
            var value = Expression.Property(Expression.Convert(row, property.DeclaringType!), property);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(value, typeof(object)), row).Compile();
        }
    }

    /// <summary>A prepared INSERT or UPDATE for one table, built from the EF model.</summary>
    private sealed class Statements : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly (SqliteParameter Parameter, Func<object, object?> Get)[] _columns;

        private Statements(SqliteCommand command, (SqliteParameter, Func<object, object?>)[] columns)
        {
            _command = command;
            _columns = columns;
        }

        public static Statements Insert(IndexDbContext db, SqliteConnection connection, SqliteTransaction transaction, Type type)
        {
            var (table, properties) = Model(db, type);
            return Prepare(connection, transaction, properties,
                $"INSERT INTO \"{table}\" ({string.Join(", ", properties.Select(p => $"\"{p.Column}\""))}) " +
                $"VALUES ({string.Join(", ", properties.Select((_, i) => $"$p{i}"))})");
        }

        public static Statements Update(IndexDbContext db, SqliteConnection connection, SqliteTransaction transaction, Type type)
        {
            var (table, properties) = Model(db, type);
            var id = properties.FindIndex(p => p.Column == "id");
            return Prepare(connection, transaction, properties,
                $"UPDATE \"{table}\" SET {string.Join(", ", properties.Select((p, i) => (p, i)).Where(x => x.i != id).Select(x => $"\"{x.p.Column}\" = $p{x.i}"))} " +
                $"WHERE id = $p{id}");
        }

        public int Run(object row)
        {
            foreach (var (parameter, get) in _columns)
            {
                parameter.Value = get(row) ?? DBNull.Value;
            }

            return _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();

        private static (string Table, List<(string Column, PropertyInfo Property)> Properties) Model(IndexDbContext db, Type type)
        {
            var entityType = db.Model.FindEntityType(type) ?? throw new InvalidOperationException($"{type.Name} isn't in the index model.");
            return (entityType.GetTableName()!, entityType.GetProperties().Select(p => (p.GetColumnName(), p.PropertyInfo!)).ToList());
        }

        private static Statements Prepare(
            SqliteConnection connection, SqliteTransaction transaction, List<(string Column, PropertyInfo Property)> properties, string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            var columns = Columns.For(properties[0].Property.DeclaringType!);
            var bound = properties
                .Select((p, i) => (command.Parameters.Add(new SqliteParameter($"$p{i}", DBNull.Value)), columns.GetterOf(p.Property)))
                .ToArray();
            command.Prepare();
            return new Statements(command, bound);
        }
    }

    /// <summary>Prepared statements for the FTS5 <c>search</c> table.</summary>
    private sealed class SearchStatements : IDisposable
    {
        private readonly SqliteCommand _insert;
        private readonly SqliteCommand _delete;

        public SearchStatements(SqliteConnection connection, SqliteTransaction transaction)
        {
            _insert = connection.CreateCommand();
            _insert.Transaction = transaction;
            _insert.CommandText = IndexSchema.InsertSearchRow;
            _insert.Parameters.Add("$rowid", SqliteType.Integer);
            _insert.Parameters.Add("$title", SqliteType.Text);
            _insert.Parameters.Add("$body", SqliteType.Text);
            _insert.Parameters.Add("$comments", SqliteType.Text);
            _insert.Parameters.Add("$kind", SqliteType.Text);
            _insert.Parameters.Add("$entity", SqliteType.Integer);
            _insert.Parameters.Add("$section", SqliteType.Integer);
            _insert.Prepare();
            _delete = connection.CreateCommand();
            _delete.Transaction = transaction;
            _delete.CommandText = "DELETE FROM search WHERE rowid = $rowid";
            _delete.Parameters.Add("$rowid", SqliteType.Integer);
            _delete.Prepare();
        }

        public int Insert(SearchRow row)
        {
            var p = _insert.Parameters;
            p["$rowid"].Value = row.RowId;
            p["$title"].Value = row.Title;
            p["$body"].Value = row.Body;
            p["$comments"].Value = row.Comments;
            p["$kind"].Value = row.Kind;
            p["$entity"].Value = row.EntityId;
            p["$section"].Value = (object?)row.SectionId ?? DBNull.Value;
            return _insert.ExecuteNonQuery();
        }

        public int Delete(long rowId)
        {
            _delete.Parameters["$rowid"].Value = rowId;
            return _delete.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _insert.Dispose();
            _delete.Dispose();
        }
    }
}
