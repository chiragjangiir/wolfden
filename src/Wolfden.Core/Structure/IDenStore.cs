namespace Wolfden.Core.Structure;

/// <summary>
/// A den's files, read and written: the den store (design.md §6, #121).
/// <see cref="IDenSource"/> is its read half, and everything the core reads
/// a den through stays an <see cref="IDenSource"/>. Writers, such as the
/// write API and the MCP write tools (#21), take an <see cref="IDenStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Paths are den-relative with <c>/</c>, as for <see cref="IDenSource"/>,
/// and every path a write names is checked first (<see cref="DenPaths.CheckWritable"/>):
/// one that could leave the den, that isn't the same file on every OS, or
/// that's hidden (F-LAY-2) is refused with <see cref="DenStoreError.InvalidPath"/>
/// before anything is touched.
/// </para>
/// <para>
/// A store that can't be written, such as the built-in packs, says so
/// through <see cref="IsReadOnly"/>, and refuses every write with
/// <see cref="DenStoreError.ReadOnly"/>. Writes keep the bytes exactly as
/// given: a store never converts line endings or encodings (format.md §12).
/// Failures the store can't foresee, such as a file another program holds
/// open or one the user may not write, are the <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> the system raised.
/// </para>
/// </remarks>
public interface IDenStore : IDenSource
{
    /// <summary>True if every write is refused with <see cref="DenStoreError.ReadOnly"/>.</summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Writes <paramref name="bytes"/> to the file at <paramref name="path"/>,
    /// exactly as given. The write is atomic: a reader, or a crash, sees the
    /// old file or the new one, never part of either. Missing folders on the
    /// way are made.
    /// </summary>
    /// <param name="path">The file's den-relative path.</param>
    /// <param name="bytes">The file's new contents.</param>
    /// <param name="mode">Whether the file must be new, must exist already, or may be either.</param>
    /// <exception cref="DenStoreException">
    /// The path is refused (<see cref="DenStoreError.InvalidPath"/>, <see cref="DenStoreError.CaseConflict"/>),
    /// the store is read-only, the file exists for <see cref="DenWriteMode.Create"/>
    /// (<see cref="DenStoreError.AlreadyExists"/>) or doesn't for <see cref="DenWriteMode.Replace"/>
    /// (<see cref="DenStoreError.NotFound"/>), or a folder is in the way (<see cref="DenStoreError.NotAFile"/>).
    /// </exception>
    void WriteFile(string path, byte[] bytes, DenWriteMode mode = DenWriteMode.CreateOrReplace);

    /// <summary>Deletes the file at <paramref name="path"/>.</summary>
    /// <exception cref="DenStoreException">The path is refused, the store is read-only, or there's no file there (<see cref="DenStoreError.NotFound"/>).</exception>
    void DeleteFile(string path);

    /// <summary>
    /// Moves or renames the file at <paramref name="from"/> to <paramref name="to"/>,
    /// which must not exist yet. Missing folders on the way are made. A
    /// rename that changes only the case of the name is allowed.
    /// </summary>
    /// <exception cref="DenStoreException">A path is refused, the store is read-only, <paramref name="from"/> isn't a file, or <paramref name="to"/> exists.</exception>
    void MoveFile(string from, string to);

    /// <summary>
    /// Moves or renames the folder at <paramref name="from"/>, with
    /// everything in it, to <paramref name="to"/>, which must not exist yet
    /// and can't be inside it. Missing folders on the way are made. A
    /// rename that changes only the case of the name is allowed.
    /// </summary>
    /// <exception cref="DenStoreException">A path is refused, the store is read-only, <paramref name="from"/> isn't a folder, or <paramref name="to"/> exists or is inside it.</exception>
    void MoveFolder(string from, string to);

    /// <summary>Deletes the folder at <paramref name="path"/>, which must be empty.</summary>
    /// <exception cref="DenStoreException">The path is refused, the store is read-only, there's no folder there, or it isn't empty (<see cref="DenStoreError.NotEmpty"/>).</exception>
    void DeleteFolder(string path);
}

/// <summary>What <see cref="IDenStore.WriteFile"/> expects to find at the path.</summary>
public enum DenWriteMode
{
    /// <summary>Make the file if it's new, or replace it.</summary>
    CreateOrReplace,

    /// <summary>The file must be new: if one is there already, it's left alone and the write fails.</summary>
    Create,

    /// <summary>The file must exist: if none is there, the write fails.</summary>
    Replace,
}

/// <summary>Why a den store refused a write, so callers such as the API and the MCP tools can map it to their own codes.</summary>
public enum DenStoreError
{
    /// <summary>The path can't name a file in the den: it's empty, absolute, leaves the den, is hidden, goes through a folder link, or isn't a name every OS allows.</summary>
    InvalidPath,

    /// <summary>The store can't be written.</summary>
    ReadOnly,

    /// <summary>There's no file or folder at the path.</summary>
    NotFound,

    /// <summary>A file or folder is already at the path.</summary>
    AlreadyExists,

    /// <summary>A name on the path differs only in case (or is another name for) one that's already there, so the two would be one file on Windows and macOS.</summary>
    CaseConflict,

    /// <summary>A folder is where a file was expected, or a file where a folder was.</summary>
    NotAFile,

    /// <summary>The folder to delete isn't empty.</summary>
    NotEmpty,
}

/// <summary>A write a den store refused, and why (<see cref="Error"/>).</summary>
public sealed class DenStoreException : IOException
{
    /// <summary>A refusal of a write to <paramref name="path"/>.</summary>
    public DenStoreException(DenStoreError error, string path, string message)
        : base(message)
    {
        Error = error;
        Path = path;
    }

    /// <summary>Why the write was refused.</summary>
    public DenStoreError Error { get; }

    /// <summary>The den-relative path the refusal is about, as the caller gave it.</summary>
    public string Path { get; }

    /// <summary>A read-only store's refusal to write <paramref name="path"/>, saying why in <paramref name="reason"/>.</summary>
    public static DenStoreException ReadOnly(string path, string reason) =>
        new(DenStoreError.ReadOnly, path, $"'{path}' can't be written: {reason}.");
}
