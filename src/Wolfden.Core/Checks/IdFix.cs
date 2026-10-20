using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Checks;

/// <summary>What <see cref="IdFix"/> did with one file.</summary>
public enum IdFixOutcome
{
    /// <summary>The file was given an id.</summary>
    Added,

    /// <summary>The file was left alone; <see cref="IdFixResult.Reason"/> says why.</summary>
    Skipped,
}

/// <summary>One file that <see cref="IdFix"/> gave an id to, or had to leave alone.</summary>
/// <param name="Path">The file, relative to the den root, with <c>/</c>.</param>
/// <param name="Outcome">Whether it was changed.</param>
/// <param name="Id">The id it was given, or null if it was skipped.</param>
/// <param name="Reason">Why it was skipped, starting in lowercase so it reads after a colon, or null.</param>
public sealed record IdFixResult(string Path, IdFixOutcome Outcome, string? Id, string? Reason);

/// <summary>
/// <c>wolfden check --fix</c> (format.md F-ID-2, F-ID-6): gives every
/// container file and entity file that loads without an <c>id</c> a new
/// one, as its first frontmatter key. It changes nothing else in the files
/// it writes. Files with errors don't load, so they aren't touched, and nor
/// are files in a final book's scope (F-BOOK-1).
/// </summary>
public static class IdFix
{
    /// <summary>Adds missing ids in the den at <paramref name="root"/>, with ids from <see cref="Ids.New()"/>.</summary>
    /// <returns>Every file that needed an id, in path order, and what happened to it.</returns>
    public static IReadOnlyList<IdFixResult> Run(string root) => Run(root, Ids.New);

    /// <summary>Adds missing ids with ids from <paramref name="newId"/>, so tests can pin them down.</summary>
    public static IReadOnlyList<IdFixResult> Run(string root, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(newId);
        var source = new FileSystemDenSource(root);
        var structure = DenStructure.Load(source);
        if (!structure.IsLoaded)
        {
            return [];
        }

        var catalog = EntityCatalog.Load(structure, source);
        var files = new List<(string Path, MarkdownFile File, Book? Book)>();
        foreach (var universe in structure.Universes)
        {
            foreach (var container in Containers(universe).Where(c => c.Id is null))
            {
                files.Add((container.FilePath, container.File, container as Book));
            }
        }

        files.AddRange(catalog.Entities.Where(e => e.Id is null).Select(e => (e.Path, e.File, e.Scope as Book)));

        var results = new List<IdFixResult>();
        foreach (var (path, file, book) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            results.Add(Fix(root, path, file, book, newId));
        }

        return results;
    }

    private static IdFixResult Fix(string root, string path, MarkdownFile file, Book? book, Func<string> newId)
    {
        if (book is { Status: BookStatus.Final })
        {
            return Skip($"it's in book '{book.Key}', which is final (frozen), so it can't be edited until the book is unfrozen (F-BOOK-1).");
        }

        var id = newId();
        MarkdownFile fixedFile;
        try
        {
            fixedFile = file.WithId(id);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or EditVerificationException)
        {
            return Skip(e.Message.Length == 0 ? "the id couldn't be added." : char.ToLowerInvariant(e.Message[0]) + e.Message[1..]);
        }

        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            // The file may have changed since it was read; don't overwrite that.
            if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(file.ToBytes()))
            {
                return Skip("it changed while the den was being checked; run the fix again.");
            }

            File.WriteAllBytes(full, fixedFile.ToBytes());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Skip($"it couldn't be written: {e.Message}");
        }

        return new IdFixResult(path, IdFixOutcome.Added, id, null);

        IdFixResult Skip(string reason) => new(path, IdFixOutcome.Skipped, null, reason);
    }

    private static IEnumerable<Container> Containers(Universe universe)
    {
        yield return universe;
        foreach (var series in universe.Series)
        {
            yield return series;
        }

        foreach (var book in universe.Books)
        {
            yield return book;
        }
    }
}
