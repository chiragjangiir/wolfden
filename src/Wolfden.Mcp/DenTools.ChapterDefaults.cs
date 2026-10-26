using System.Text.Json;
using Wolfden.Core.Documents;
using Wolfden.Core.Editing;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Mcp;

/// <summary>
/// Reading and editing a container's <c>chapter_defaults</c> (format.md
/// F-CTR-5, types.md F-CHAP-5, #151): the defaults of a universe, a series,
/// or a book, which its books' chapters inherit field by field unless a
/// nearer container or the chapter itself sets the field.
/// </summary>
/// <remarks>
/// <para>
/// The write rules are the other write tools': an expected hash, a check of
/// the file as the den reads it, and nothing in a final book written.
/// </para>
/// <para>
/// <b>Frozen books below</b> (design choice, #151). A book's own
/// <c>book.md</c> is in its scope, so a final book's defaults are refused
/// outright (F-BOOK-1). A series or universe isn't in a book's scope, but
/// its defaults reach the book's chapters, so an edit there is refused if it
/// would change the effective value of any chapter of a final book; one the
/// final book doesn't see (its chapters, or its own defaults, set the field)
/// is made. That's the rule <c>rename_section</c> follows for links in a
/// final book: what a frozen book says never changes underneath it.
/// </para>
/// <para>
/// The check fails closed. Books are matched by path, not key (two books
/// can share a key, F-KEY-5); a final book whose chapters can't be paired
/// before and after, and a <c>book.md</c> below that the den leaves out for
/// errors but that says <c>status: final</c> (or whose frontmatter can't be
/// read), stop the edit. A link in a value is compared by what it reaches
/// from the container that holds it (types.md F-CHAP-4), so the same text
/// naming another entity counts as a change. The <c>book.md</c> and
/// <c>series.md</c> files below are compared again just before the write,
/// so a book frozen meanwhile isn't missed.
/// </para>
/// </remarks>
public sealed partial class DenTools
{
    /// <summary>Runs just before <see cref="UpdateChapterDefaults"/> saves; tests use it to change a file meanwhile.</summary>
    internal Action? BeforeDefaultsSave { get; init; }

    /// <summary>
    /// A universe's, series', or book's <c>chapter_defaults</c>: each field it
    /// sets, what its books would inherit from above without it, the books
    /// below it, and the hash of its file for
    /// <see cref="UpdateChapterDefaults"/>.
    /// </summary>
    public GetChapterDefaultsResult GetChapterDefaults(string? universe = null, string? series = null, string? book = null)
    {
        var universeKey = LocateContainer(universe, series, book);
        var container = FindContainer(DenStructure.Load(Snapshot()), universeKey, series, book);
        var view = DefaultsOf(container);
        return new GetChapterDefaultsResult(
            ToolJson.Version, universeKey, view.Kind, view.Key, view.Path, HashOf(container.File.ToBytes()), container is Book { Status: BookStatus.Final },
            view.Fields, BooksBelow(container).Select(b => new BookBelowView(b.Key, b.Status == BookStatus.Final, b.Chapters.Count, b.FilePath)).ToList());
    }

    /// <summary>
    /// Changes a container's <c>chapter_defaults</c>: <c>set</c> a field,
    /// <c>add</c> an item to a list field, <c>remove</c> an item (the last one
    /// leaves <c>[]</c>, "none here", which stops inheritance from above,
    /// format.md F-RT-6), or <c>clear</c> the field, so the books go back to
    /// inheriting it from the container above. The result says what each
    /// book's chapters have now.
    /// </summary>
    public WriteResult UpdateChapterDefaults(string expectedHash, JsonElement changes, string? universe = null, string? series = null, string? book = null)
    {
        var hash = RequireHash(expectedHash);
        var ops = Changes(changes, chapter: true);
        var universeKey = LocateContainer(universe, series, book);
        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var target = FindContainer(structure, universeKey, series, book);
            var path = target.FilePath;
            var original = target.File.ToBytes();
            CheckHash(path, original, hash);
            if (target is Book { Status: BookStatus.Final })
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen, $"Book '{target.Key}' is final (frozen), so its book.md can't be edited until it's unfrozen (F-BOOK-1).");
            }

            var file = target.File;
            var touched = new List<string>();
            foreach (var op in ops)
            {
                var field = op.Field!;
                touched.Add(field);
                string[] fieldPath = ["chapter_defaults", field];
                file = Apply(op.Op switch
                {
                    "set" => () => file.WithFrontmatterValue(fieldPath, op.Value!),
                    "add" => () => file.WithNewListItem(fieldPath, op.Value!, op.Index),
                    "remove" => () => file.WithoutChapterDefaultItem(field, ItemToRemove(file, fieldPath, op, "The default")),
                    _ => () => ClearDefault(file, target, field),
                });
            }

            touched = touched.Distinct().ToList();
            if (file.ToBytes().AsSpan().SequenceEqual(original))
            {
                // A write that changes nothing never adds an id (F-ID-6).
                return new WriteResult(
                    ToolJson.Version, false, path, HashOf(original), null, [$"Nothing changed: {path[(path.LastIndexOf('/') + 1)..]} already says that."], [], [],
                    ChapterDefaults: DefaultsOf(target));
            }

            var idAdded = target.Id is null ? AddId(ref file) : null;
            var bytes = file.ToBytes();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = bytes };
            var check = Check(den, structure, files);
            var after = FindContainer(check.Structure, universeKey, series, book);
            var overlay = new OverlayDenSource(den, files);
            var edit = new DefaultsEdit(
                target, after,
                new Lazy<LinkResolver>(() => new LinkResolver(EntityCatalog.Load(structure, den))),
                new Lazy<LinkResolver>(() => new LinkResolver(EntityCatalog.Load(check.Structure, overlay))));

            // What the check below depends on, compared again just before the write.
            var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = original };
            foreach (var (other, otherBytes) in FilesBelow(den, target))
            {
                originals.TryAdd(other, otherBytes);
            }

            CheckFinalBooksBelow(edit, touched);
            CheckUnloadedBooksBelow(den, structure, target);
            BeforeDefaultsSave?.Invoke();
            Save(files, originals, created: null);

            var effects = DefaultEffects(edit, touched);
            if (idAdded is not null)
            {
                effects.Add($"The file had no id, so it was given {idAdded} (F-ID-6).");
            }

            return new WriteResult(
                ToolJson.Version, true, path, HashOf(bytes), null, effects, [], Warnings(check), IdAdded: idAdded, ChapterDefaults: DefaultsOf(after));
        }
    }

    /// <summary>An edit of a container's defaults: the container before and after, and link resolvers over the den before and after.</summary>
    private sealed record DefaultsEdit(Container Before, Container After, Lazy<LinkResolver> BeforeLinks, Lazy<LinkResolver> AfterLinks);

    /// <summary>The universe a <c>chapter_defaults</c> call is about, after checking that the series or book it names is there.</summary>
    private string LocateContainer(string? universe, string? series, string? book) => WithIndex(index =>
    {
        var db = index.Context;
        var u = ChooseUniverse(db, universe, null, allowAll: false)!;
        CheckFilter(db, u, Filter(series?.Trim(), book?.Trim()));
        return u.Key;
    });

    private static Container FindContainer(DenStructure structure, string universe, string? series, string? book)
    {
        var u = structure.FindUniverse(universe) ?? throw new DenToolException(
            ToolErrorCodes.UnknownUniverse, $"Universe '{universe}' doesn't load now; it may have changed since it was read. Call check to see why.");
        if (book is not null)
        {
            return FindBook(structure, universe, book.Trim());
        }

        if (series is not null)
        {
            return u.Series.FirstOrDefault(s => s.Key == series.Trim()) ?? throw new DenToolException(
                ToolErrorCodes.UnknownScope, $"Universe '{universe}' has no series '{series.Trim()}' now; it may have changed since it was read.");
        }

        return u;
    }

    /// <summary>The books whose chapters a container's <c>chapter_defaults</c> reach, in story order.</summary>
    private static IReadOnlyList<Book> BooksBelow(Container container) => container switch
    {
        Universe u => u.Books,
        Series s => s.Books,
        Book b => [b],
        _ => [],
    };

    /// <summary>
    /// The <c>book.md</c> and <c>series.md</c> files in a container's folder
    /// (format.md §6), loaded or not, with their bytes now: what decides
    /// which chapters below are final and what they inherit.
    /// </summary>
    private static IEnumerable<(string Path, byte[] Bytes)> FilesBelow(IDenSource den, Container container)
    {
        var folders = new List<string>();
        if (container is Universe)
        {
            foreach (var s in den.GetDirectories($"{container.Path}/series"))
            {
                folders.Add($"{container.Path}/series/{s}");
                folders.AddRange(den.GetDirectories($"{container.Path}/series/{s}/books").Select(b => $"{container.Path}/series/{s}/books/{b}"));
            }

            folders.AddRange(den.GetDirectories($"{container.Path}/books").Select(b => $"{container.Path}/books/{b}"));
        }
        else if (container is Series)
        {
            folders.AddRange(den.GetDirectories($"{container.Path}/books").Select(b => $"{container.Path}/books/{b}"));
        }

        foreach (var folder in folders)
        {
            foreach (var name in new[] { "book.md", "series.md" })
            {
                if (den.TryReadFile($"{folder}/{name}") is { } bytes)
                {
                    yield return ($"{folder}/{name}", bytes);
                }
            }
        }
    }

    private static MarkdownFile ClearDefault(MarkdownFile file, Container container, string field)
    {
        if ((file.GetFrontmatterValue("chapter_defaults") as YamlMapping)?.ContainsKey(field) != true)
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument,
                $"{Capitalized(Where(container))} doesn't set '{field}', so there's nothing to clear: its books already inherit it from above.");
        }

        return file.WithoutFrontmatterValue(["chapter_defaults", field]);
    }

    /// <summary>
    /// Refuses an edit of a series' or universe's defaults that would change
    /// what a chapter of a final book has (F-BOOK-1), failing closed: see the
    /// remarks on this class.
    /// </summary>
    private static void CheckFinalBooksBelow(DefaultsEdit edit, IReadOnlyList<string> fields)
    {
        var problems = new List<string>();
        var afterPaths = BooksBelow(edit.After).Select(b => b.FilePath).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in BooksBelow(edit.Before).Where(b => b.Status == BookStatus.Final && !afterPaths.Contains(b.FilePath)))
        {
            problems.Add($"{BookName(gone)} would no longer load");
        }

        foreach (var book in BooksBelow(edit.After).Where(b => b.Status == BookStatus.Final))
        {
            var was = BookBefore(edit, book);
            if (was is null || was.Chapters.Count != book.Chapters.Count || book.Chapters.Any(c => ChapterBefore(was, c) is null))
            {
                problems.Add($"{BookName(book)}, whose chapters couldn't be compared before and after the edit");
                continue;
            }

            foreach (var field in fields)
            {
                var changed = book.Chapters.Where(c => Changed(edit, ChapterBefore(was, c), c, field)).ToList();
                if (changed.Count > 0)
                {
                    problems.Add($"{BookName(book)}, {field} of {Chapters(book, changed)}");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new DenToolException(
                ToolErrorCodes.Frozen,
                $"The edit would change what chapters of a final (frozen) book have, which can't change until the book is unfrozen (F-BOOK-1): " +
                $"{string.Join("; ", problems)}. Nothing was changed. To leave the final book as it is, set the value in the chapter_defaults " +
                "of each draft book instead, or ask the writer whether to unfreeze it.");
        }
    }

    /// <summary>
    /// Refuses the edit if a <c>book.md</c> below that the den leaves out for
    /// errors (it, or its series, doesn't load) says <c>status: final</c>, or
    /// its frontmatter can't be read, so it may be: its chapters would inherit
    /// the edit once it's fixed.
    /// </summary>
    private static void CheckUnloadedBooksBelow(IDenSource den, DenStructure structure, Container container)
    {
        var loaded = structure.Universes.SelectMany(u => u.Books).Select(b => b.FilePath).ToHashSet(StringComparer.Ordinal);
        var suspect = new List<string>();
        foreach (var (path, bytes) in FilesBelow(den, container).Where(f => f.Path.EndsWith("/book.md", StringComparison.Ordinal) && !loaded.Contains(f.Path)))
        {
            // Parsing never throws; frontmatter that doesn't parse is null.
            var frontmatter = MarkdownFile.Parse(bytes, requireFrontmatter: true).Frontmatter;
            if (frontmatter is null || frontmatter["status"] is not (null or YamlScalar { IsNull: true } or YamlScalar { Text: "draft" }))
            {
                suspect.Add(path);
            }
        }

        if (suspect.Count > 0)
        {
            throw new DenToolException(
                ToolErrorCodes.Frozen,
                $"These books have errors, so the den leaves them out, but they're marked final (or their status can't be read), and their chapters " +
                $"would inherit this edit once they're fixed: {string.Join(", ", suspect)}. Nothing was changed. Call check to see the errors, " +
                "and fix them first (F-BOOK-1).");
        }
    }

    /// <summary>What the edit means for the container and for each chapter below it, field by field.</summary>
    private static List<string> DefaultEffects(DefaultsEdit edit, IReadOnlyList<string> fields)
    {
        var effects = new List<string>();
        var after = edit.After;
        var where = Capitalized(Where(after));
        foreach (var name in fields)
        {
            var own = after.ChapterDefaults.GetValueOrDefault(name);
            var above = Above(after, name);
            var aboveText = above is null ? null : $"{Describe(above)} from {Where(above.DefaultFrom!)}";
            var written = (after.File.Frontmatter?["chapter_defaults"] as YamlMapping)?[name] is { } node && node is not YamlScalar { IsNull: true };
            effects.Add(own switch
            {
                { IsNone: true } => $"{where} now sets {name}: [] (\"none here\"), so chapters below that don't set their own have no {name}" +
                    (aboveText is null ? "." : $", rather than inheriting {aboveText}. To inherit that again, clear the field."),
                not null when above is not null && Describe(own) == Describe(above) => $"{where} now sets {name}: {Describe(own)}, as {Where(above.DefaultFrom!)} does.",
                not null => $"{where} now sets {name}: {Describe(own)}" + (aboveText is null ? "." : $", in place of {aboveText}."),
                null when written => $"{where} writes {name}, but the value doesn't fit the field, so it's ignored and chapters inherit as if it weren't there (see the warnings).",
                null => $"{where} no longer sets {name}" + (aboveText is null ? ", and no chapter_defaults above it does." : $", so its books inherit {aboveText}."),
            });

            var withChapters = BooksBelow(after).Where(b => b.Chapters.Count > 0).ToList();
            if (withChapters.Count == 0)
            {
                effects.Add($"No book below has chapters yet, so no chapter's {name} changes now.");
                continue;
            }

            foreach (var book in withChapters)
            {
                effects.Add(BookEffect(edit, book, name));
            }
        }

        return effects;
    }

    /// <summary>
    /// One book's chapters after the edit, in runs: "chapters 3–9 now have
    /// pov [[mina-harker]]; chapter 12 keeps its own".
    /// </summary>
    private static string BookEffect(DefaultsEdit edit, Book book, string name)
    {
        var was = BookBefore(edit, book);
        var chain = book.ScopeChain.Select(c => c.FilePath).ToList();
        var editedRank = chain.IndexOf(edit.After.FilePath);
        var labels = book.Chapters.Select(c =>
        {
            var now = c.Fields.GetValueOrDefault(name);
            if (Changed(edit, was is null ? null : ChapterBefore(was, c), c, name))
            {
                return now is null ? $"now have no {name}: nothing sets one"
                    : now.IsNone ? $"now have no {name} ([])"
                    : $"now have {name} {Describe(now)}";
            }

            if (c.OwnFields.ContainsKey(name))
            {
                return "own";
            }

            return now?.DefaultFrom is { } from && chain.IndexOf(from.FilePath) is var rank && rank >= 0 && rank < editedRank
                ? $"keep {Describe(now)} from {Where(from)}"
                : null;
        }).ToList();

        var parts = new List<string>();
        // The chapters that change first, then the ones that keep what they had.
        foreach (var label in labels.Where(l => l is not null).Distinct().OrderBy(l => l!.StartsWith("now ", StringComparison.Ordinal) ? 0 : 1))
        {
            var chapters = book.Chapters.Where((_, i) => labels[i] == label).ToList();
            var several = chapters.Count > 1;
            var text = label == "own" ? (several ? "keep their own" : "keeps its own")
                : several ? label!
                : label!.StartsWith("keep ", StringComparison.Ordinal) ? "keeps " + label["keep ".Length..]
                : "now has " + label["now have ".Length..];
            parts.Add($"{Chapters(book, chapters)} {text}");
        }

        var bookName = Capitalized(BookName(book));
        return parts.Count == 0
            ? $"{bookName}: no chapter's {name} changes."
            : $"{bookName}: {string.Join("; ", parts)}.";
    }

    /// <summary>The same book before the edit, matched by path (book keys can repeat, F-KEY-5), or null.</summary>
    private static Book? BookBefore(DefaultsEdit edit, Book after) =>
        edit.Before.Universe.Books.FirstOrDefault(b => b.FilePath == after.FilePath);

    /// <summary>The same chapter before the edit: the one at the same place with the same key, or null.</summary>
    private static Chapter? ChapterBefore(Book before, Chapter after) =>
        after.Index < before.Chapters.Count && before.Chapters[after.Index].Key == after.Key ? before.Chapters[after.Index] : null;

    /// <summary>"book 'ash'", with its folder when another book of the universe has the same key.</summary>
    private static string BookName(Book book) =>
        book.Universe.Books.Count(b => b.Key == book.Key) > 1 ? $"book '{book.Key}' ({book.Path})" : $"book '{book.Key}'";

    /// <summary>Chapters as runs in book order: "chapter 12", "chapters 3–9, 12".</summary>
    private static string Chapters(Book book, IReadOnlyList<Chapter> chapters)
    {
        var runs = new List<string>();
        var indexes = chapters.Select(c => c.Index).Order().ToList();
        for (var i = 0; i < indexes.Count;)
        {
            var j = i;
            while (j + 1 < indexes.Count && indexes[j + 1] == indexes[j] + 1)
            {
                j++;
            }

            var first = book.Chapters[indexes[i]].Key;
            runs.Add(j == i ? first : $"{first}–{book.Chapters[indexes[j]].Key}");
            i = j + 1;
        }

        return (chapters.Count == 1 ? "chapter " : "chapters ") + string.Join(", ", runs);
    }

    /// <summary>True if the chapter's effective value for the field differs after the edit; a chapter with nothing to compare to has changed.</summary>
    private static bool Changed(DefaultsEdit edit, Chapter? before, Chapter after, string name) =>
        before is null || EffectiveKey(before, name, edit.BeforeLinks) != EffectiveKey(after, name, edit.AfterLinks);

    /// <summary>
    /// A chapter's effective value for a field, as text to compare, or null
    /// if nothing sets it. A link in it is compared by the entity it reaches
    /// from the container that holds the value (types.md F-CHAP-4), since the
    /// same text in another container's defaults can name another entity.
    /// </summary>
    private static string? EffectiveKey(Chapter chapter, string name, Lazy<LinkResolver> links)
    {
        if (chapter.Fields.GetValueOrDefault(name) is not { } value)
        {
            return null;
        }

        if (value.IsNone)
        {
            return "[]";
        }

        var scope = value.DefaultFrom ?? chapter.Book;
        return string.Join("\n", value.Items.Select(item =>
        {
            var text = JsonOf(item).GetRawText();
            if (item is not YamlScalar s || !s.Text.Contains("[[", StringComparison.Ordinal) || LinkSyntax.FindLinks(s.Text) is not { Count: > 0 } found)
            {
                return text;
            }

            return text + " -> " + string.Join(" ", found.Select(l => links.Value.Resolve(scope, l.Target) is { Entity: { } e }
                ? $"{e.Path}#{l.Heading}"
                : $"?{l.Target}@{scope.FilePath}"));
        }));
    }

    /// <summary>What a container's books inherit for a field from the containers above it.</summary>
    private static ChapterFieldValue? Above(Container container, string name) =>
        container.ScopeChain.Skip(1).Select(c => c.ChapterDefaults.GetValueOrDefault(name)).FirstOrDefault(v => v is not null);

    private static ChapterDefaultsView DefaultsOf(Container container)
    {
        var written = container.File.Frontmatter?["chapter_defaults"] as YamlMapping;
        var names = container.ScopeChain.SelectMany(c => c.ChapterDefaults.Keys)
            .Concat(written?.Entries.Select(e => e.KeyText).OfType<string>() ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var fields = names.Select(name =>
        {
            var own = container.ChapterDefaults.GetValueOrDefault(name);
            var above = Above(container, name);

            // Written here but ignored (F-CHAP-3): a null, or a value that doesn't fit the field.
            var ignored = own is null && written?.ContainsKey(name) == true;
            return new ChapterDefaultFieldView(
                name,
                own is null ? null : JsonOf(own.Value),
                own?.IsNone == true,
                above is null ? null : JsonOf(above.Value),
                above?.DefaultFrom?.Kind.ToString().ToLowerInvariant(),
                above?.DefaultFrom?.Key,
                ignored ? true : null,
                ignored ? JsonOf(written![name]) : null);
        }).ToList();
        return new ChapterDefaultsView(container.Kind.ToString().ToLowerInvariant(), container.Key, container.FilePath, fields);
    }

    /// <summary>A chapter field's value in words: its items, or "none ([])".</summary>
    private static string Describe(ChapterFieldValue value) =>
        value.IsNone ? "none ([])" : string.Join(", ", value.Items.Select(i => i is YamlScalar s ? s.Text : JsonOf(i).GetRawText()));

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
