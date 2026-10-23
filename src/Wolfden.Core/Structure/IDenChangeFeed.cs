namespace Wolfden.Core.Structure;

/// <summary>
/// The watch half of the den store (#121): reports changes to a den's
/// files as they happen, whoever makes them (an editor, git, an AI agent,
/// or the store's own writes). The index's watcher (<c>DenWatcher</c>)
/// listens through it, so a store kept somewhere other than a local folder
/// can bring its own way of noticing changes.
/// </summary>
public interface IDenChangeFeed
{
    /// <summary>
    /// Starts reporting changes to <paramref name="observer"/> until the
    /// returned handle is disposed, or the watch breaks (<see cref="IDenChangeObserver.Broken"/>).
    /// Calls to the observer can come from any thread, though not two at once
    /// for one watch.
    /// </summary>
    /// <exception cref="IOException">The watch couldn't be started, such as when the system's limit on watches is reached.</exception>
    IDisposable Watch(IDenChangeObserver observer);
}

/// <summary>What an <see cref="IDenChangeFeed"/> reports to.</summary>
public interface IDenChangeObserver
{
    /// <summary>A file or folder changed.</summary>
    void Changed(DenChange change);

    /// <summary>Changes were lost, as when too many came at once, but the watch goes on. Anything kept from the files may be out of date.</summary>
    void Lost();

    /// <summary>
    /// The watch stopped for good, such as when the drive went away; no more
    /// changes come through it. To go on, start a new watch.
    /// </summary>
    void Broken(Exception error);
}

/// <summary>How a path changed.</summary>
public enum DenChangeKind
{
    /// <summary>A file or folder was made.</summary>
    Created,

    /// <summary>A file's contents changed (or, on some systems, a folder's listing).</summary>
    Changed,

    /// <summary>A file or folder was deleted.</summary>
    Deleted,

    /// <summary>A file or folder was renamed or moved from <see cref="DenChange.OldPath"/>.</summary>
    Renamed,
}

/// <summary>
/// One change to a den: a den-relative path with <c>/</c>, and for
/// <see cref="DenChangeKind.Renamed"/> the path it had before. Paths can be
/// hidden ones, such as a temporary file; it's for the listener to leave
/// out what isn't part of the den (F-LAY-2).
/// </summary>
public sealed record DenChange(DenChangeKind Kind, string Path, string? OldPath = null);
