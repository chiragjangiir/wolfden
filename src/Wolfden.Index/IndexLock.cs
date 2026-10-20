using System.Collections.Concurrent;

namespace Wolfden.Index;

/// <summary>
/// One lock per den in this process, held while its index is written: by a
/// full rebuild, and by a file watcher's update (#14). So a query's own
/// rebuild (<see cref="IndexBuilder.EnsureUpToDate"/>) and a watcher in the
/// same process take turns. Another process is kept out by SQLite's locks
/// and the <see cref="IndexSchema.WriteIdKey"/> check.
/// </summary>
internal static class IndexLock
{
    private static readonly ConcurrentDictionary<string, object> Locks =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The lock for the den at <paramref name="root"/>, a full path.</summary>
    public static object For(string root) => Locks.GetOrAdd(Path.TrimEndingDirectorySeparator(root), _ => new object());
}
