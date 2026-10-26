using System.Text.Json;
using Wolfden.Core.Documents;
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
/// </remarks>
public sealed partial class DenTools
{
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
            view.Fields, BooksBelow(container).Select(b => new BookBelowView(b.Key, b.Status == BookStatus.Final, b.Chapters.Count)).ToList());
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
            CheckFinalBooksBelow(target, after, touched);
            Save(files, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = original }, created: null);

            var effects = DefaultEffects(target, after, touched);
            if (idAdded is not null)
            {
                effects.Add($"The file had no id, so it was given {idAdded} (F-ID-6).");
            }

            return new WriteResult(
                ToolJson.Version, true, path, HashOf(bytes), null, effects, [], Warnings(check), IdAdded: idAdded, ChapterDefaults: DefaultsOf(after));
        }
    }

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
    /// what a chapter of a final book has (F-BOOK-1): see the remarks on this class.
    /// </summary>
    private static void CheckFinalBooksBelow(Container before, Container after, IReadOnlyList<string> fields)
    {
        var problems = new List<string>();
        foreach (var book in BooksBelow(after).Where(b => b.Status == BookStatus.Final))
        {
            var was = before.Universe.FindBook(book.Key);
            foreach (var field in fields)
            {
                var changed = book.Chapters.Where(c => EffectiveKey(was?.FindChapter(c.Key), field) != EffectiveKey(c, field)).ToList();
                if (changed.Count > 0)
                {
                    problems.Add($"book '{book.Key}', {field} of {Chapters(book, changed)}");
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

    /// <summary>What the edit means for the container and for each chapter below it, field by field.</summary>
    private static List<string> DefaultEffects(Container before, Container after, IReadOnlyList<string> fields)
    {
        var effects = new List<string>();
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
                effects.Add(BookEffect(before, after, book, name));
            }
        }

        return effects;
    }

    /// <summary>
    /// One book's chapters after the edit, in runs: "chapters 3–9 now have
    /// pov [[mina-harker]]; chapter 12 keeps its own".
    /// </summary>
    private static string BookEffect(Container before, Container edited, Book book, string name)
    {
        var was = before.Universe.FindBook(book.Key);
        var chain = book.ScopeChain.Select(c => c.FilePath).ToList();
        var editedRank = chain.IndexOf(edited.FilePath);
        var labels = book.Chapters.Select(c =>
        {
            var now = c.Fields.GetValueOrDefault(name);
            if (EffectiveKey(was?.FindChapter(c.Key), name) != EffectiveKey(c, name))
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

        return parts.Count == 0
            ? $"Book '{book.Key}': no chapter's {name} changes."
            : $"Book '{book.Key}': {string.Join("; ", parts)}.";
    }

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

    /// <summary>A chapter's effective value for a field, as text to compare, or null if nothing sets it.</summary>
    private static string? EffectiveKey(Chapter? chapter, string name) =>
        chapter?.Fields.GetValueOrDefault(name) is { } value
            ? value.IsNone ? "[]" : string.Join("\n", value.Items.Select(i => JsonOf(i).GetRawText()))
            : null;

    /// <summary>What a container's books inherit for a field from the containers above it.</summary>
    private static ChapterFieldValue? Above(Container container, string name) =>
        container.ScopeChain.Skip(1).Select(c => c.ChapterDefaults.GetValueOrDefault(name)).FirstOrDefault(v => v is not null);

    private static ChapterDefaultsView DefaultsOf(Container container)
    {
        var names = container.ScopeChain.SelectMany(c => c.ChapterDefaults.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var fields = names.Select(name =>
        {
            var own = container.ChapterDefaults.GetValueOrDefault(name);
            var above = Above(container, name);
            return new ChapterDefaultFieldView(
                name,
                own is null ? null : JsonOf(own.Value),
                own?.IsNone == true,
                above is null ? null : JsonOf(above.Value),
                above?.DefaultFrom?.Kind.ToString().ToLowerInvariant(),
                above?.DefaultFrom?.Key);
        }).ToList();
        return new ChapterDefaultsView(container.Kind.ToString().ToLowerInvariant(), container.Key, container.FilePath, fields);
    }

    /// <summary>A chapter field's value in words: its items, or "none ([])".</summary>
    private static string Describe(ChapterFieldValue value) =>
        value.IsNone ? "none ([])" : string.Join(", ", value.Items.Select(i => i is YamlScalar s ? s.Text : JsonOf(i).GetRawText()));

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
