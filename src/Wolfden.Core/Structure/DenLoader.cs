using System.Globalization;
using System.Text.RegularExpressions;
using Wolfden.Core.Documents;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Structure;

/// <summary>Reads a den's structure for <see cref="DenStructure.Load(IDenSource)"/>.</summary>
internal sealed partial class DenLoader(DenStructure den, IDenSource source)
{
    private const string DenFile = "den.yaml";

    public void Load()
    {
        if (!source.FileExists(DenFile))
        {
            // §5.1: den.yaml is required, and without it there's no format (F-DEN-1).
            den.Add(DenFile, new Diagnostic("F-DEN-1", DiagnosticSeverity.Error, null,
                "This folder has no den.yaml, so it isn't a den. Add one with 'format: 1'."));
            return;
        }

        var file = YamlFile.Parse(source.ReadFile(DenFile));
        foreach (var d in file.Diagnostics)
        {
            den.Add(DenFile, d);
        }

        if (file.HasErrors)
        {
            return;
        }

        if (ReadFormat(file) is not int format)
        {
            return;
        }

        // The format parsed, so it's known even if a later key keeps the den
        // from loading. From here on every key is read, so each problem is
        // reported (F-GEN-3). A bad format stops the read above.
        den.Format = format;
        var settings = ReadSettings(file);
        den.Authors = ReadAuthors(file);
        if (ReadPacks(file) is not { } packs || !settings)
        {
            return;
        }

        den.SetLoaded();

        // Pack files are den configuration, read before any note (types.md §1).
        den.Types = DenTypes.Load(source, packs);
        foreach (var d in den.Types.Diagnostics)
        {
            den.Add(d.Path, d.Diagnostic);
        }

        foreach (var key in Folders("universes"))
        {
            if (LoadUniverse(key) is { } universe)
            {
                den.AddUniverse(universe);
                new ChapterFieldReader(den, universe).Read();
            }
        }
    }

    /// <summary>
    /// F-DEN-4: <c>packs</c> is an optional list of pack keys, each naming a
    /// built-in pack or a den-local one (a folder <c>packs/&lt;key&gt;/</c>
    /// with a <c>pack.yaml</c>), with no repeats. Returns null on an error,
    /// which keeps the den from loading.
    /// </summary>
    private List<string>? ReadPacks(YamlFile file)
    {
        var node = file.GetValue("packs");
        if (node is null or YamlScalar { IsNull: true })
        {
            return [];
        }

        if (node is not YamlSequence list)
        {
            den.Add(DenFile, new Diagnostic("F-DEN-4", DiagnosticSeverity.Error, node.Line,
                "packs must be a list of pack keys, such as 'packs: [starfall]'."));
            return null;
        }

        var result = new List<string>();
        var ok = true;
        foreach (var item in list.Items)
        {
            if (item is not YamlScalar { IsNull: false } scalar)
            {
                den.Add(DenFile, new Diagnostic("F-DEN-4", DiagnosticSeverity.Error, item.Line, "Each entry in packs must be a pack key."));
                ok = false;
                continue;
            }

            var key = scalar.Text;
            if (result.Contains(key, StringComparer.Ordinal))
            {
                den.Add(DenFile, new Diagnostic("F-DEN-4", DiagnosticSeverity.Error, item.Line, $"'{key}' is listed more than once in packs."));
                ok = false;
                continue;
            }

            var isLocal = Keys.IsKey(key) && source.FileExists($"packs/{key}/pack.yaml");
            if (!isLocal && !BuiltInPacks.Instance.Keys.Contains(key))
            {
                var message = Keys.IsKey(key) && source.FileExists($"packs/{key}/pack.yml")
                    ? $"packs names '{key}', but packs/{key} has pack.yml, which isn't read: rename it to pack.yaml."
                    : $"packs names '{key}', but there's no built-in pack '{key}' and no folder packs/{key}/ with a pack.yaml.";
                den.Add(DenFile, new Diagnostic("F-DEN-4", DiagnosticSeverity.Error, item.Line, message));
                ok = false;
                continue;
            }

            result.Add(key);
        }

        return ok ? result : null;
    }

    /// <summary>
    /// F-DEN-3: <c>authors</c> is an optional mapping from author key
    /// (F-KEY-1) to an Author Identity, a mapping with a non-empty
    /// <c>name</c>. Other keys in an identity are kept. Any other shape is a
    /// warning: an <c>authors</c> that isn't a mapping is ignored, and an
    /// identity with an invalid key or without a name is skipped, so the den
    /// still loads. Returns the author keys that loaded, in file order.
    /// </summary>
    private List<string> ReadAuthors(YamlFile file)
    {
        var keys = new List<string>();
        var node = file.GetValue("authors");
        if (node is null or YamlScalar { IsNull: true })
        {
            return keys;
        }

        if (node is not YamlMapping map)
        {
            den.Add(DenFile, new Diagnostic("F-DEN-3", DiagnosticSeverity.Warning, node.Line,
                "authors must be a mapping from author key to an identity with a name, such as 'authors: { wren-calder: { name: Wren Calder } }', so it's ignored and the den has no authors."));
            return keys;
        }

        foreach (var entry in map.Entries)
        {
            var key = entry.KeyText ?? "";
            var ok = true;
            if (!Keys.IsKey(key))
            {
                den.Add(DenFile, new Diagnostic("F-KEY-1", DiagnosticSeverity.Warning, entry.Key.Line,
                    $"'{key}' isn't a valid author key: use 1 to 64 lowercase letters and digits, with single hyphens between words. The author is skipped."));
                ok = false;
            }

            if (entry.Value is not YamlMapping identity)
            {
                den.Add(DenFile, new Diagnostic("F-DEN-3", DiagnosticSeverity.Warning, entry.Value.Line,
                    $"Author '{key}' must be a mapping with a name, such as '{{ name: Wren Calder }}', so it's skipped."));
                ok = false;
            }
            else if (identity["name"] is not YamlScalar { IsNull: false } name || name.Text.Trim().Length == 0)
            {
                den.Add(DenFile, new Diagnostic("F-DEN-3", DiagnosticSeverity.Warning, identity["name"]?.Line ?? entry.Key.Line,
                    $"Author '{key}' needs a non-empty name, so it's skipped."));
                ok = false;
            }

            if (ok)
            {
                keys.Add(key);
            }
            else
            {
                den.SkippedAuthors.Add(key);
            }
        }

        return keys;
    }

    /// <summary>F-DEN-1: <c>format</c> is a required integer, from 1 up to the highest this reader supports.</summary>
    private int? ReadFormat(YamlFile file)
    {
        var node = file.GetValue("format");
        if (node is not YamlScalar { IsNull: false, Style: YamlScalarStyle.Plain } scalar || !IsDecimalInteger(scalar.Text))
        {
            den.Add(DenFile, new Diagnostic("F-DEN-1", DiagnosticSeverity.Error, node?.Line ?? 1,
                node is null ? "den.yaml has no format. Add 'format: 1'." : "format must be an integer, such as 'format: 1'."));
            return null;
        }

        var text = scalar.Text.TrimStart('+');
        var tooBig = !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var format);
        if (tooBig ? !text.StartsWith('-') : format > DenStructure.SupportedFormat)
        {
            den.Add(DenFile, new Diagnostic("F-DEN-1", DiagnosticSeverity.Error, node.Line,
                $"den format {text} is newer than this version of Wolfden, which reads format {DenStructure.SupportedFormat}."));
            return null;
        }

        if (tooBig || format < 1)
        {
            den.Add(DenFile, new Diagnostic("F-DEN-1", DiagnosticSeverity.Error, node.Line,
                $"den format {text} doesn't exist: formats start at 1."));
            return null;
        }

        return format;
    }

    /// <summary>
    /// F-DEN-6 and F-DEN-7: <c>bookkeeping_sections</c> and
    /// <c>spoiler_checks</c>. Returns false if <c>bookkeeping_sections</c>
    /// has the wrong shape, an error that keeps the den from loading.
    /// </summary>
    private bool ReadSettings(YamlFile file)
    {
        switch (file.GetValue("spoiler_checks"))
        {
            // A null is the same as leaving it out, as for order and books.
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar scalar when scalar.AsBoolean() is bool on:
                den.SpoilerChecks = on;
                break;
            case var node:
                den.Add(DenFile, new Diagnostic("F-DEN-7", DiagnosticSeverity.Warning, node.Line,
                    $"spoiler_checks must be true or false, not '{Describe(node)}', so the spoiler checks stay off."));
                break;
        }

        var patterns = file.GetValue("bookkeeping_sections");
        if (patterns is null or YamlScalar { IsNull: true })
        {
            return true;
        }

        var valid = patterns is YamlSequence list
            && list.Items.All(i => i is YamlScalar { IsNull: false } s && s.Text.Trim().Length > 0);
        if (!valid)
        {
            den.Add(DenFile, new Diagnostic("F-DEN-6", DiagnosticSeverity.Error, patterns.Line,
                "bookkeeping_sections must be a list of non-empty heading patterns, such as '[\"Scratch\", \"* working notes\"]'."));
            return false;
        }

        var items = ((YamlSequence)patterns).Items.Cast<YamlScalar>().ToList();
        foreach (var item in items.Where(i => i.Text.All(c => c is '*' || char.IsWhiteSpace(c))))
        {
            den.Add(DenFile, new Diagnostic("F-DEN-6", DiagnosticSeverity.Warning, item.Line,
                $"The bookkeeping pattern '{item.Text}' matches every section, so every section is bookkeeping."));
        }

        den.BookkeepingSections = items.Select(i => i.Text).ToList();
        return true;
    }

    private static bool IsDecimalInteger(string text)
    {
        var digits = text.Length > 0 && text[0] is '+' or '-' ? text[1..] : text;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit);
    }

    private Universe? LoadUniverse(string key)
    {
        var path = $"universes/{key}";
        if (ReadContainer(ContainerKind.Universe, path, key) is not { } read)
        {
            return null;
        }

        var order = ReadKeyList(read, "order", "F-UNI-1", "F-UNI-2", "series or standalone book");
        if (read.HasErrors)
        {
            return null;
        }

        var universe = new Universe(den, key, path, read.File, read.Name, read.Id);
        var series = Folders($"{path}/series").Select(k => LoadSeries(universe, k)).OfType<WorkInfo>().ToList();
        var standalone = Folders($"{path}/books").Select(k => LoadBook(universe, null, k)).OfType<WorkInfo>().ToList();

        // F-KEY-5: book keys are unique across the universe, and a series key
        // can't be a standalone book key. Each clash warns on every container involved.
        var allBooks = series.SelectMany(s => s.Books).Concat(standalone).ToList();
        foreach (var group in allBooks.GroupBy(b => b.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            foreach (var book in group)
            {
                den.Add(book.FilePath, new Diagnostic("F-KEY-5", DiagnosticSeverity.Warning, null,
                    $"Book key '{group.Key}' is used by {group.Count()} books in universe '{key}' ({string.Join(", ", group.Select(b => b.Path))}). Positions naming it don't resolve until it's unique."));
            }
        }

        var clashes = series.Select(s => s.Key).Intersect(standalone.Select(b => b.Key), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var work in series.Concat(standalone).Where(w => clashes.Contains(w.Key)))
        {
            den.Add(work.FilePath, new Diagnostic("F-KEY-5", DiagnosticSeverity.Warning, null,
                $"'{work.Key}' is both a series and a standalone book in universe '{key}', so order can't tell them apart. Entries in order that name it are ignored."));
        }

        var works = series.Concat(standalone).OrderBy(w => w.Key, StringComparer.Ordinal).ThenBy(w => w.Kind).ToList();
        var folders = Folders($"{path}/series").Concat(Folders($"{path}/books")).ToHashSet(StringComparer.Ordinal);
        var ordered = Arrange(read, order, works, clashes, folders, "order", "F-UNI-2", "F-UNI-3", "series or standalone book");

        // Story order (F-ORD-1): walk the works, putting each series' books in place.
        var slot = 0;
        var loadedWorks = new List<Container>();
        var books = new List<Book>();
        foreach (var work in ordered)
        {
            switch (work.Loaded)
            {
                case Series s:
                    if (s.Books.Count == 0)
                    {
                        s.Slot = slot++;
                    }

                    foreach (var book in s.Books)
                    {
                        book.Slot = slot++;
                        books.Add(book);
                    }

                    loadedWorks.Add(s);
                    break;
                case Book b:
                    b.Slot = slot++;
                    books.Add(b);
                    loadedWorks.Add(b);
                    break;
            }
        }

        universe.Works = loadedWorks;
        universe.Books = books;
        universe.IndexBooks(allBooks.Select(b => b.Key), books);
        return universe;
    }

    private WorkInfo? LoadSeries(Universe universe, string key)
    {
        var path = $"{universe.Path}/series/{key}";
        if (ReadContainer(ContainerKind.Series, path, key) is not { } read)
        {
            return null;
        }

        var listed = ReadKeyList(read, "books", "F-SER-1", "F-SER-2", "book");
        if (read.HasErrors)
        {
            // The series and everything under it are left out (F-GEN-2), so its
            // books aren't read and get no diagnostics of their own. Their keys
            // still count, for F-KEY-5 and for the "has errors" F-POS-4 message.
            var unread = Folders($"{path}/books")
                .Where(k => Keys.IsContainerKey(k) && source.FileExists($"{path}/books/{k}/book.md"))
                .Select(k => new WorkInfo(ContainerKind.Book, k, $"{path}/books/{k}", $"{path}/books/{k}/book.md", null, []))
                .ToList();
            return new WorkInfo(ContainerKind.Series, key, path, read.FilePath, null, unread);
        }

        var series = new Series(universe, key, path, read.File, read.Name, read.Id);
        var books = Folders($"{path}/books").Select(k => LoadBook(universe, series, k)).OfType<WorkInfo>()
            .OrderBy(b => b.Key, StringComparer.Ordinal).ToList();
        var ordered = Arrange(read, listed, books, [], Folders($"{path}/books").ToHashSet(StringComparer.Ordinal), "books", "F-SER-2", "F-SER-3", "book");
        series.Books = ordered.Select(b => b.Loaded).OfType<Book>().ToList();
        return new WorkInfo(ContainerKind.Series, key, path, read.FilePath, series, books);
    }

    private WorkInfo? LoadBook(Universe universe, Series? series, string key)
    {
        var path = series is null ? $"{universe.Path}/books/{key}" : $"{series.Path}/books/{key}";
        if (ReadContainer(ContainerKind.Book, path, key) is not { } read)
        {
            return null;
        }

        var status = BookStatus.Draft;
        var statusNode = read.Frontmatter?["status"];
        switch (statusNode)
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar { Text: "draft" }:
                break;
            case YamlScalar { Text: "final" }:
                status = BookStatus.Final;
                break;
            default:
                read.Error("F-BOOK-1", statusNode.Line, $"A book's status is draft or final, not '{Describe(statusNode)}'.");
                break;
        }

        var chapters = ReadChapters(read);
        if (read.HasErrors)
        {
            return new WorkInfo(ContainerKind.Book, key, path, read.FilePath, null, []);
        }

        var book = new Book(universe, series, key, path, read.File, read.Name, read.Id, status);
        book.Chapters = chapters.Select((c, i) => new Chapter(book, c.Key, i, c.Number, c.Title, c.Line) { FieldsNode = c.Fields }).ToList();
        return new WorkInfo(ContainerKind.Book, key, path, read.FilePath, book, []);
    }

    /// <summary>F-BOOK-2 to F-BOOK-5: the chapter list.</summary>
    private List<ChapterInfo> ReadChapters(ContainerRead read)
    {
        var result = new List<ChapterInfo>();
        var node = read.Frontmatter?["chapters"];
        if (node is null or YamlScalar { IsNull: true })
        {
            return result;
        }

        if (node is not YamlSequence list)
        {
            read.Error("F-BOOK-2", node.Line, "chapters must be a list of chapter items, such as 'chapters: [prologue, 1, 2]'.");
            return result;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        long? lastNumber = null;
        foreach (var item in list.Items)
        {
            YamlNode? keyNode = item switch
            {
                YamlScalar { IsNull: false } => item,
                YamlMapping map => map["key"],
                _ => null,
            };

            if (keyNode is not YamlScalar { IsNull: false } keyScalar)
            {
                read.Error("F-BOOK-3", item.Line, item is YamlMapping
                    ? "This chapter item has no key. Add one, such as '{ key: \"3\", number: 3 }'."
                    : "A chapter item is a chapter key or a mapping with a key.");
                continue;
            }

            var key = keyScalar.Text;
            if (!Keys.IsKey(key))
            {
                read.Error("F-KEY-1", keyNode.Line,
                    $"'{key}' isn't a valid chapter key: use 1 to 64 lowercase letters and digits, with single hyphens between words.");
                continue;
            }

            if (!Keys.IsChapterKey(key))
            {
                read.Error("F-KEY-2", keyNode.Line,
                    $"'{key}' can't be a chapter key: 'r' followed by a digit is reserved for outline-row positions.");
                continue;
            }

            if (seen.TryGetValue(key, out var firstLine))
            {
                read.Error("F-BOOK-4", keyNode.Line, $"Chapter key '{key}' is repeated (first on line {firstLine}).");
                continue;
            }

            seen[key] = keyNode.Line;
            long? number = null;
            string? title = null;
            if (item is YamlMapping mapping)
            {
                switch (mapping["number"])
                {
                    case null or YamlScalar { IsNull: true }:
                        break;
                    case YamlScalar { Style: YamlScalarStyle.Plain } n when TryParseNonNegativeInt(n.Text, out var value):
                        number = value;
                        break;
                    case var n:
                        read.Warning("F-BOOK-3", n.Line,
                            $"Chapter '{key}' has number '{Describe(n)}', which isn't a non-negative integer, so it has no number.");
                        break;
                }

                title = mapping["title"] is YamlScalar { IsNull: false } t ? t.Text : null;
            }

            if (number is long current)
            {
                if (lastNumber is long previous && current <= previous)
                {
                    read.Warning("F-BOOK-5", item.Line,
                        $"Chapter '{key}' has number {current}, which doesn't come after {previous}. Numbers should increase down the list.");
                }

                lastNumber = current;
            }

            result.Add(new ChapterInfo(key, number, title, item.Line, (item as YamlMapping)?["fields"]));
        }

        return result;
    }

    /// <summary>
    /// Reads <c>order</c> or <c>books</c>: a list of keys. A shape other than
    /// a list is an error, and so is a repeated entry (F-UNI-2, F-SER-2).
    /// Entries that aren't scalars are kept, so <see cref="Arrange"/> can
    /// warn about them.
    /// </summary>
    private static List<YamlNode> ReadKeyList(ContainerRead read, string field, string shapeRule, string repeatRule, string what)
    {
        var node = read.Frontmatter?[field];
        if (node is null or YamlScalar { IsNull: true })
        {
            return [];
        }

        if (node is not YamlSequence list)
        {
            read.Error(shapeRule, node.Line, $"{field} must be a list of {what} keys, such as '{field}: [{(field == "order" ? "long-dark, a-quiet-moon" : "ember, ash")}]'.");
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.Items)
        {
            if (item is YamlScalar { IsNull: false } scalar && !seen.Add(scalar.Text))
            {
                read.Error(repeatRule, item.Line, $"'{scalar.Text}' is listed more than once in {field}.");
            }
        }

        return list.Items.ToList();
    }

    /// <summary>
    /// Puts works in the order a list gives (F-UNI-1, F-SER-1). Entries that
    /// name nothing, or a clashing key, are warnings and are ignored. An entry
    /// naming a folder that isn't a container (F-LAY-4, F-KEY-1) is ignored
    /// without a second warning, since the folder already has one. Works
    /// the list leaves out are warnings when there's more than one work, and
    /// come after the listed ones by key in ASCII order (F-UNI-3, F-SER-3).
    /// </summary>
    private List<WorkInfo> Arrange(ContainerRead read, List<YamlNode> entries, List<WorkInfo> works,
        HashSet<string> clashes, HashSet<string> folders, string field, string entryRule, string missingRule, string what)
    {
        var listed = new List<WorkInfo>();
        foreach (var entry in entries)
        {
            if (entry is not YamlScalar { IsNull: false } scalar)
            {
                read.Warning(entryRule, entry.Line, $"An entry in {field} must be a {what} key. This one is ignored.");
                continue;
            }

            if (clashes.Contains(scalar.Text))
            {
                read.Warning(entryRule, entry.Line,
                    $"'{scalar.Text}' is both a series and a standalone book (F-KEY-5), so this entry in {field} is ignored.");
                continue;
            }

            var work = works.FirstOrDefault(w => w.Key == scalar.Text);
            if (work is null && !folders.Contains(scalar.Text))
            {
                read.Warning(entryRule, entry.Line, $"{field} names '{scalar.Text}', but there's no {what} '{scalar.Text}' here. The entry is ignored.");
            }
            else if (work is not null && !listed.Contains(work))
            {
                listed.Add(work);
            }
        }

        var missing = works.Where(w => !listed.Contains(w)).ToList();
        if (works.Count > 1)
        {
            var line = read.Frontmatter?[field]?.Line;
            foreach (var work in missing)
            {
                read.Warning(missingRule, line,
                    $"{field} doesn't list {(work.Kind == ContainerKind.Series ? "series" : "book")} '{work.Key}', so it comes after the listed ones, by key.");
            }
        }

        return listed.Concat(missing).ToList();
    }

    /// <summary>
    /// Reads a container folder's file and the rules every container shares
    /// (F-KEY-1, F-KEY-2, F-LAY-4, F-CTR-1, F-ID-1). Returns null if the
    /// folder isn't a container that can be read at all.
    /// </summary>
    private ContainerRead? ReadContainer(ContainerKind kind, string path, string key)
    {
        if (!Keys.IsContainerKey(key))
        {
            var (rule, message) = Keys.IsKey(key)
                ? ("F-KEY-2", $"'{key}' isn't a valid {Kind(kind)} key: it must start with a letter.")
                : ("F-KEY-1", $"'{key}' isn't a valid {Kind(kind)} key: use 1 to 64 lowercase letters and digits, with single hyphens between words.");
            den.Add(path, new Diagnostic(rule, DiagnosticSeverity.Error, null, message + " The folder is skipped."));
            return null;
        }

        var filePath = $"{path}/{Container.FileName(kind)}";
        if (!source.FileExists(filePath))
        {
            den.Add(path, new Diagnostic("F-LAY-4", DiagnosticSeverity.Error, null,
                $"The {Kind(kind)} folder '{key}' has no {Container.FileName(kind)}, so it's skipped."));
            return null;
        }

        var file = MarkdownFile.Parse(source.ReadFile(filePath), requireFrontmatter: true);
        var read = new ContainerRead(den, filePath, file);
        foreach (var d in file.Diagnostics)
        {
            den.Add(filePath, d);
        }

        var nameNode = read.Frontmatter?["name"];
        if (nameNode is YamlScalar { IsNull: false, Text.Length: > 0 } name)
        {
            read.Name = name.Text;
        }
        else if (file.HasFrontmatter)
        {
            read.Error("F-CTR-1", nameNode?.Line ?? 1, $"A {Kind(kind)} needs a non-empty name.");
        }

        switch (read.Frontmatter?["id"])
        {
            case null or YamlScalar { IsNull: true }:
                break;
            case YamlScalar id when Keys.IsId(id.Text):
                read.Id = id.Text;
                break;
            case var id:
                read.Error("F-ID-1", id.Line, $"'{Describe(id)}' isn't a valid id: an id is a ULID, 26 uppercase Crockford base32 characters.");
                break;
        }

        return read;
    }

    /// <summary>The names of the folders in <paramref name="path"/>, without hidden ones (F-LAY-2), in ASCII order.</summary>
    private IEnumerable<string> Folders(string path) =>
        source.GetDirectories(path).Where(n => !n.StartsWith('.')).Order(StringComparer.Ordinal).ToList();

    private static string Kind(ContainerKind kind) => kind.ToString().ToLowerInvariant();

    private static string Describe(YamlNode node) => node switch
    {
        YamlScalar s => s.Text,
        YamlSequence => "a list",
        _ => "a mapping",
    };

    /// <summary>A YAML 1.2 core-schema integer that isn't negative.</summary>
    private static bool TryParseNonNegativeInt(string text, out long value)
    {
        value = 0;
        if (text.StartsWith("0o", StringComparison.Ordinal))
        {
            return text.Length > 2 && text[2..].All(c => c is >= '0' and <= '7') && TryConvert(text[2..], 8, out value);
        }

        if (text.StartsWith("0x", StringComparison.Ordinal))
        {
            return text.Length > 2 && text[2..].All(char.IsAsciiHexDigit) && TryConvert(text[2..], 16, out value);
        }

        var digits = text.StartsWith('+') ? text[1..] : text;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit)
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryConvert(string digits, int radix, out long value)
    {
        try
        {
            value = Convert.ToInt64(digits, radix);
            return value >= 0;
        }
        catch (OverflowException)
        {
            value = 0;
            return false;
        }
    }

    private sealed record ChapterInfo(string Key, long? Number, string? Title, int Line, YamlNode? Fields);

    /// <summary>A series or book folder that has a container file, loaded or not.</summary>
    /// <param name="Loaded">The container, or null if it has errors (F-GEN-2).</param>
    /// <param name="Books">For a series, its book folders (unread, if the series has errors).</param>
    private sealed record WorkInfo(ContainerKind Kind, string Key, string Path, string FilePath, Container? Loaded, List<WorkInfo> Books);

    /// <summary>A container file being read, and whether it has errors so far.</summary>
    private sealed class ContainerRead(DenStructure den, string filePath, MarkdownFile file)
    {
        private bool _hasErrors = file.HasErrors;

        public string FilePath => filePath;

        public MarkdownFile File => file;

        public YamlMapping? Frontmatter => file.Frontmatter;

        public string Name { get; set; } = "";

        public string? Id { get; set; }

        public bool HasErrors => _hasErrors;

        public void Error(string rule, int? line, string message)
        {
            _hasErrors = true;
            den.Add(filePath, new Diagnostic(rule, DiagnosticSeverity.Error, line, message));
        }

        public void Warning(string rule, int? line, string message) =>
            den.Add(filePath, new Diagnostic(rule, DiagnosticSeverity.Warning, line, message));
    }
}
