namespace Wolfden.Index;

/// <summary>How a file watcher brought the index up to date (#14).</summary>
public enum IndexUpdateKind
{
    /// <summary>A full rebuild: the first build, or a change that a rebuild handles best (see <see cref="IndexUpdate.Reason"/>).</summary>
    Rebuilt,

    /// <summary>Only the changed files were read again, and only the rows that changed were written.</summary>
    Updated,
}

/// <summary>What one batch of changes did to the index, from <see cref="DenWatcher.Updated"/>.</summary>
/// <param name="Kind">A rebuild or an incremental update.</param>
/// <param name="Reason">For a rebuild, why, such as "den.yaml changed"; null for an update.</param>
/// <param name="Files">The den-relative paths of the changed files the index reads, in ASCII order. Empty for a rebuild.</param>
/// <param name="Rows">The rows written: inserted, updated, and deleted. For a rebuild, every row.</param>
/// <param name="Elapsed">How long it took, from reading the files to committing.</param>
/// <param name="Summary">What the index holds now.</param>
public sealed record IndexUpdate(IndexUpdateKind Kind, string? Reason, IReadOnlyList<string> Files, int Rows, TimeSpan Elapsed, IndexSummary Summary);

/// <summary>What an index holds.</summary>
/// <param name="DenLoaded">True if <c>den.yaml</c> loaded (F-DEN-1). If not, nothing else is indexed.</param>
/// <param name="Files">The files read.</param>
/// <param name="Universes">The universes indexed.</param>
/// <param name="Entities">The entities indexed.</param>
/// <param name="Links">The links indexed.</param>
/// <param name="Errors">Errors found while loading.</param>
/// <param name="Warnings">Warnings found while loading.</param>
public sealed record IndexSummary(bool DenLoaded, int Files, int Universes, int Entities, int Links, int Errors, int Warnings);
