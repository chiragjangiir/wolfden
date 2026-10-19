using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Wolfden.Index;

/// <summary>
/// The EF Core model of the index (design.md §7). Tables and columns use
/// snake_case names. The full-text table, <c>search</c>, is an FTS5 virtual
/// table outside the model; <see cref="IndexSchema"/> creates it.
/// </summary>
public sealed class IndexDbContext : DbContext
{
    private readonly SqliteConnection _connection;

    /// <summary>A context over an open or openable connection. The caller owns the connection.</summary>
    public IndexDbContext(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary>Facts about the index: its schema version and the den format it was built for.</summary>
    public DbSet<MetaRecord> Meta => Set<MetaRecord>();

    /// <summary>The den (one row).</summary>
    public DbSet<DenRecord> Dens => Set<DenRecord>();

    /// <summary>The files the index was built from.</summary>
    public DbSet<FileRecord> Files => Set<FileRecord>();

    /// <summary>Universes, series, and books.</summary>
    public DbSet<ContainerRecord> Containers => Set<ContainerRecord>();

    /// <summary>Chapters of books.</summary>
    public DbSet<ChapterRecord> Chapters => Set<ChapterRecord>();

    /// <summary>Entities that loaded.</summary>
    public DbSet<EntityRecord> Entities => Set<EntityRecord>();

    /// <summary>Entity aliases.</summary>
    public DbSet<AliasRecord> Aliases => Set<AliasRecord>();

    /// <summary>Field values and entries.</summary>
    public DbSet<FieldValueRecord> FieldValues => Set<FieldValueRecord>();

    /// <summary>Field defaults of the enabled types.</summary>
    public DbSet<FieldDefaultRecord> FieldDefaults => Set<FieldDefaultRecord>();

    /// <summary>Entity sections.</summary>
    public DbSet<SectionRecord> Sections => Set<SectionRecord>();

    /// <summary>Links.</summary>
    public DbSet<LinkRecord> Links => Set<LinkRecord>();

    /// <summary>Outline rows.</summary>
    public DbSet<OutlineRowRecord> OutlineRows => Set<OutlineRowRecord>();

    /// <summary>Diagnostics.</summary>
    public DbSet<DiagnosticRecord> Diagnostics => Set<DiagnosticRecord>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlite(_connection);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetaRecord>(e =>
        {
            e.ToTable("meta");
            e.HasKey(m => m.Key);
        });

        modelBuilder.Entity<DenRecord>().ToTable("den");

        modelBuilder.Entity<FileRecord>(e =>
        {
            e.ToTable("files");
            e.HasIndex(f => f.Path).IsUnique();
        });

        modelBuilder.Entity<ContainerRecord>(e =>
        {
            e.ToTable("containers");
            e.HasOne<FileRecord>().WithMany().HasForeignKey(c => c.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.UniverseId, c.Kind, c.Key });
        });

        modelBuilder.Entity<ChapterRecord>(e =>
        {
            e.ToTable("chapters");
            e.HasOne<ContainerRecord>().WithMany().HasForeignKey(c => c.BookId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.BookId, c.Index }).IsUnique();
        });

        modelBuilder.Entity<EntityRecord>(e =>
        {
            e.ToTable("entities");
            e.HasOne<FileRecord>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Path).IsUnique();
            e.HasIndex(x => x.Ulid);
            e.HasIndex(x => x.Key);
            e.HasIndex(x => new { x.UniverseId, x.NameNormalized });
            e.HasIndex(x => new { x.UniverseId, x.Slug });
            e.HasIndex(x => new { x.UniverseId, x.Type });
        });

        modelBuilder.Entity<AliasRecord>(e =>
        {
            e.ToTable("aliases");
            e.HasOne<EntityRecord>().WithMany().HasForeignKey(a => a.EntityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => a.AliasNormalized);
            e.HasIndex(a => new { a.EntityId, a.Order }).IsUnique();
        });

        modelBuilder.Entity<FieldValueRecord>(e =>
        {
            e.ToTable("field_values");
            e.HasOne<EntityRecord>().WithMany().HasForeignKey(v => v.EntityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(v => new { v.EntityId, v.FieldOrder, v.EntryIndex });
            e.HasIndex(v => new { v.UniverseId, v.FromOrdinal, v.UntilOrdinal });
        });

        modelBuilder.Entity<FieldDefaultRecord>(e =>
        {
            e.ToTable("field_defaults");
            e.HasIndex(d => new { d.TypeKey, d.Field }).IsUnique();
        });

        modelBuilder.Entity<SectionRecord>(e =>
        {
            e.ToTable("sections");
            e.HasOne<EntityRecord>().WithMany().HasForeignKey(s => s.EntityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.EntityId, s.Index }).IsUnique();
            e.HasIndex(s => new { s.UniverseId, s.FromOrdinal, s.UntilOrdinal });
        });

        modelBuilder.Entity<LinkRecord>(e =>
        {
            e.ToTable("links");
            e.HasOne<FileRecord>().WithMany().HasForeignKey(l => l.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.TargetKey);
            e.HasIndex(l => new { l.FileId, l.Line, l.Column });
            e.HasIndex(l => l.SourceEntityId);
        });

        modelBuilder.Entity<OutlineRowRecord>(e =>
        {
            e.ToTable("outline_rows");
            e.HasOne<FileRecord>().WithMany().HasForeignKey(r => r.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.BookId, r.Order });
            e.HasIndex(r => new { r.UniverseId, r.RowId });
        });

        modelBuilder.Entity<DiagnosticRecord>(e =>
        {
            e.ToTable("diagnostics");
            e.HasIndex(d => d.Path);
        });

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
                if (property.Name == "Id")
                {
                    // Ids are assigned in a fixed order, so two rebuilds give identical rows.
                    property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
        }
    }

    /// <summary><c>FromOrdinal</c> becomes <c>from_ordinal</c>.</summary>
    internal static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }
}
