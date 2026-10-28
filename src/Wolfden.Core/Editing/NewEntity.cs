using System.Globalization;
using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Editing;

/// <summary>What a new entity starts with, besides its type, scope, and id (types.md F-TPL-6, step 3).</summary>
/// <param name="Name">The entity's name, as the writer gave it.</param>
public sealed record NewEntityRequest(string Name)
{
    /// <summary>The slug (file name without <c>.md</c>), or null to make one from the name.</summary>
    public string? Slug { get; init; }

    /// <summary>Aliases, or null for the template's.</summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>The status, or null for the template's, or <c>draft</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Where it enters canon, as a position, or null for the template's.</summary>
    public string? From { get; init; }

    /// <summary>Field values, by field name, in the order given. Each replaces the template's value for that field.</summary>
    public IReadOnlyList<KeyValuePair<string, YamlValue>>? Fields { get; init; }
}

/// <summary>A new entity file, not yet written.</summary>
/// <param name="Path">Its den-relative path.</param>
/// <param name="Slug">Its slug.</param>
/// <param name="File">Its contents.</param>
public sealed record NewEntityFile(string Path, string Slug, MarkdownFile File);

/// <summary>
/// Makes a new entity from its type's template (types.md §7.2, F-TPL-6):
/// the path from the type's folder and a slug made from the name, the
/// frontmatter in template order (F-TPL-7) with the caller's values in
/// place of the template's, and the template's body with <c>{{name}}</c>
/// replaced (F-TPL-4). It never writes: the caller checks the file
/// (<see cref="DenWriteCheck"/>) and saves it.
/// </summary>
public static class NewEntity
{
    private static readonly string[] OwnKeys = ["id", "type", "name", "aliases", "status", "from", "fields"];

    /// <summary>
    /// The longest name a writer gives a new, copied, or renamed entity, in
    /// UTF-16 code units: room for any real name or title, and a typo-sized
    /// paste stays a clear error rather than a heading that runs on. The
    /// name is never part of the path (the slug is cut to 64 characters,
    /// F-TPL-6), so this has nothing to do with path limits.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Why <paramref name="name"/> can't be an entity's name, or null if it
    /// can: it's empty, longer than <see cref="MaxNameLength"/>, or holds a
    /// character that would break the one-line <c># name</c> heading, a
    /// control character or a Unicode line or paragraph separator
    /// (U+2028, U+2029), which a template's body would turn into a line
    /// break.
    /// </summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "it's empty";
        }

        if (name.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        {
            return "it has a line break or another control character in it, and a name is one line of text";
        }

        var trimmed = name.Trim();
        return trimmed.Length > MaxNameLength ? $"it's {trimmed.Length} characters long, and a name is at most {MaxNameLength}" : null;
    }

    /// <summary>The folder a new entity of <paramref name="type"/> goes in, in <paramref name="scope"/> (F-TPL-6, step 1).</summary>
    public static string FolderFor(Container scope, TypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(type);
        return $"{scope.Path}/entities/{type.Plural}";
    }

    /// <summary>
    /// A slug made from <paramref name="name"/> as F-TPL-6 says: lowercased,
    /// letters that don't decompose spelled out (<c>æ</c> as <c>ae</c>, and so
    /// on), accents dropped, each run of other characters made one <c>-</c>,
    /// and cut to 64 characters at a word boundary. Empty if nothing is left.
    /// </summary>
    public static string Slugify(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lower = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            lower.Append(c switch
            {
                'æ' => "ae",
                'œ' => "oe",
                'ø' => "o",
                'ß' => "ss",
                'þ' => "th",
                'ð' or 'đ' => "d",
                'ł' => "l",
                _ => c.ToString(),
            });
        }

        var slug = new StringBuilder();
        var pending = false;
        foreach (var c in lower.ToString().Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pending && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pending = false;
                slug.Append(c);
            }
            else
            {
                pending = true;
            }
        }

        return Cut(slug.ToString(), Keys.MaxLength);
    }

    /// <summary>True if <c>&lt;slug&gt;.md</c> matches a file already in <paramref name="folder"/>, compared ignoring case (F-TPL-6).</summary>
    public static bool IsTaken(IDenSource den, string folder, string slug)
    {
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(slug);
        var name = slug + ".md";

        // A name no OS-portable den can hold, such as Windows's 'con.md', counts as taken.
        return DenPaths.Problem($"{folder}/{name}") is not null
            || den.GetFiles(folder).Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
            || den.GetDirectories(folder).Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="slug"/>, or if it's taken in <paramref name="folder"/>,
    /// the first of <c>-2</c>, <c>-3</c>, and so on after it that isn't, with
    /// the slug cut back so the whole stays within 64 characters (F-TPL-6).
    /// </summary>
    public static string Unique(IDenSource den, string folder, string slug)
    {
        if (!IsTaken(den, folder, slug))
        {
            return slug;
        }

        for (var n = 2; ; n++)
        {
            var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            var candidate = Cut(slug, Keys.MaxLength - suffix.Length) + suffix;
            if (!IsTaken(den, folder, candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The new entity of <paramref name="type"/> in <paramref name="scope"/>,
    /// with id <paramref name="id"/>: its path, and its file as F-TPL-6 writes
    /// it. The slug is the request's, or one made from the name (the type key
    /// if that leaves nothing), made unique in the folder.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, the slug or id isn't valid, a field name isn't one, or the slug asked for is taken.</exception>
    /// <exception cref="EditVerificationException">The file didn't read back as built (a bug).</exception>
    public static NewEntityFile Create(IDenSource den, Container scope, TypeDefinition type, string id, NewEntityRequest request)
    {
        ArgumentNullException.ThrowIfNull(den);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name;
        if (NameProblem(name) is { } nameProblem)
        {
            throw new ArgumentException($"That can't be a name: {nameProblem}.", nameof(request));
        }

        if (request.Aliases?.Any(a => string.IsNullOrWhiteSpace(a) || a.Any(char.IsControl)) == true)
        {
            throw new ArgumentException("Each alias is one line of text, not empty (F-ENT-4).", nameof(request));
        }

        if (!Keys.IsId(id))
        {
            throw new ArgumentException($"'{id}' isn't a valid id.", nameof(id));
        }

        var folder = FolderFor(scope, type);
        string slug;
        if (request.Slug is { } asked)
        {
            if (!Keys.IsKey(asked))
            {
                throw new ArgumentException($"'{asked}' isn't a valid slug: lowercase letters and digits, with single hyphens between words, up to 64 characters.", nameof(request));
            }

            if (DenPaths.Problem($"{folder}/{asked}.md") is { } problem)
            {
                throw new ArgumentException($"'{asked}' can't be a file name: {problem}.", nameof(request));
            }

            if (IsTaken(den, folder, asked))
            {
                throw new ArgumentException($"'{folder}/{asked}.md' is taken: there's already a file of that name (compared ignoring case).", nameof(request));
            }

            slug = asked;
        }
        else
        {
            var made = Slugify(name);
            slug = Unique(den, folder, made.Length > 0 ? made : type.Key);
        }

        var fields = request.Fields ?? [];
        foreach (var (field, _) in fields)
        {
            if (!YamlScalarValue.IsSafeKey(field) || field.Length > 64 || !char.IsAsciiLetterLower(field[0]) || field.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            {
                throw new ArgumentException($"'{field}' isn't a valid field name: lowercase letters, digits, and '_', starting with a letter (F-FLD-1).", nameof(request));
            }
        }

        if (fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw new ArgumentException("A field is given more than once.", nameof(request));
        }

        var template = type.Template.File;
        var text = Frontmatter(template, type, id, request, fields) + template.Body.Replace("{{name}}", name, StringComparison.Ordinal).ReplaceLineEndings("\n");
        var file = MarkdownFile.Parse(Encoding.UTF8.GetBytes(text), requireFrontmatter: true);
        Verify(template, file, type, id, request, fields);
        return new NewEntityFile($"{folder}/{slug}.md", slug, file);
    }

    /// <summary>The frontmatter, fences included, in template order (F-TPL-7).</summary>
    private static string Frontmatter(MarkdownFile template, TypeDefinition type, string id, NewEntityRequest request, IReadOnlyList<KeyValuePair<string, YamlValue>> fields)
    {
        var (opening, entries) = Entries(template);
        var sb = new StringBuilder("---\n").Append(opening);
        sb.Append("id: ").Append(id).Append('\n');
        sb.Append("type: ").Append(YamlScalarValue.String(type.Key).Render(null)).Append('\n');
        sb.Append("name: ").Append(YamlScalarValue.String(request.Name).Render(null)).Append('\n');

        if (request.Aliases is { } aliases)
        {
            var list = new YamlSequenceValue();
            foreach (var alias in aliases)
            {
                list.Add(YamlScalarValue.String(alias));
            }

            sb.Append("aliases: ").Append(list.RenderFlow(null, null)).Append('\n');
        }
        else if (entries.TryGetValue("aliases", out var raw))
        {
            sb.Append(raw.Text);
        }

        if (request.Status is { } status)
        {
            sb.Append("status: ").Append(YamlScalarValue.String(status).Render(null)).Append('\n');
        }
        else if (entries.TryGetValue("status", out var raw) && raw.Entry.Value is YamlScalar { IsNull: false })
        {
            sb.Append(raw.Text);
        }
        else
        {
            sb.Append("status: draft\n");
        }

        if (request.From is { } from)
        {
            sb.Append("from: ").Append(YamlScalarValue.String(from).Render(null)).Append('\n');
        }
        else if (entries.TryGetValue("from", out var raw))
        {
            sb.Append(raw.Text);
        }

        if (fields.Count > 0)
        {
            sb.Append(Fields(template, type, entries.GetValueOrDefault("fields"), fields));
        }
        else if (entries.TryGetValue("fields", out var raw))
        {
            sb.Append(raw.Text);
        }

        foreach (var (key, raw) in entries.Where(e => !OwnKeys.Contains(e.Key)).OrderBy(e => e.Value.Order))
        {
            sb.Append(raw.Text);
        }

        return sb.Append("---\n").ToString();
    }

    /// <summary>A top-level template entry: its lines as written (LF line ends), its order, and the parsed entry.</summary>
    private sealed record RawEntry(string Text, int Order, YamlEntry Entry);

    /// <summary>
    /// The template's frontmatter: the comment lines before its first key,
    /// and each top-level entry's lines, keyed by name. An entry's lines run
    /// to the next entry's first line, so comment lines between entries go
    /// with the entry above them.
    /// </summary>
    private static (string Opening, Dictionary<string, RawEntry> Entries) Entries(MarkdownFile template)
    {
        var entries = new Dictionary<string, RawEntry>(StringComparer.Ordinal);
        if (template.Frontmatter is not { } map || template.SourceForReading is not { } source)
        {
            return ("", entries);
        }

        var regionStart = source.Lines[1].Start;
        var regionEnd = source.Lines[source.LineIndexOf(template.BodyStart - 1)].Start;
        if (map.IsFlow)
        {
            // `{ status: draft }` on one line: each entry is written out on its own.
            for (var i = 0; i < map.Entries.Count; i++)
            {
                var entry = map.Entries[i];
                if (entry.KeyText is { } key)
                {
                    entries.TryAdd(key, new RawEntry($"{key}: {ValueText(source, entry.Value)}".TrimEnd() + "\n", i, entry));
                }
            }

            return ("", entries);
        }

        var starts = map.Entries.Select(e => source.Lines[source.LineIndexOf(e.Key.Start)].Start).ToList();
        var opening = source.Text[regionStart..starts[0]].ReplaceLineEndings("\n");
        for (var i = 0; i < map.Entries.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : regionEnd;
            var text = source.Text[starts[i]..end].ReplaceLineEndings("\n");
            if (!text.EndsWith('\n'))
            {
                text += "\n";
            }

            if (map.Entries[i].KeyText is { } key)
            {
                entries.TryAdd(key, new RawEntry(text, i, map.Entries[i]));
            }
        }

        return (opening, entries);
    }

    /// <summary>
    /// <c>fields:</c> with the template's entries and the caller's: the
    /// template's keep their order and are written as the template writes
    /// them unless the caller gives a value, and the caller's other fields
    /// go in template order (F-TPL-6, step 3; F-TPL-7).
    /// </summary>
    private static string Fields(MarkdownFile template, TypeDefinition type, RawEntry? templateFields, IReadOnlyList<KeyValuePair<string, YamlValue>> given)
    {
        var source = template.SourceForReading;
        var children = new List<(string Key, string? Raw)>();
        var indent = 2;
        if (templateFields?.Entry.Value is YamlMapping { Entries.Count: > 0 } map && source is not null)
        {
            if (map.IsFlow)
            {
                foreach (var e in map.Entries.Where(e => e.KeyText is not null))
                {
                    children.Add((e.KeyText!, $"{e.KeyText}: {ValueText(source, e.Value)}".TrimEnd() + "\n"));
                }
            }
            else
            {
                indent = map.Entries[0].Key.Start - source.Lines[source.LineIndexOf(map.Entries[0].Key.Start)].Start;
                var starts = map.Entries.Select(e => source.Lines[source.LineIndexOf(e.Key.Start)].Start).ToList();
                var end = templateFields.Entry.Value.LastLineOffset;
                var last = source.Lines[source.LineIndexOf(Math.Max(end, map.Entries[^1].Key.ContentEnd))];
                for (var i = 0; i < map.Entries.Count; i++)
                {
                    var stop = i + 1 < starts.Count ? starts[i + 1] : last.EndIncludingTerminator;
                    var text = source.Text[starts[i]..stop].ReplaceLineEndings("\n");
                    children.Add((map.Entries[i].KeyText ?? "", text.EndsWith('\n') ? text : text + "\n"));
                }
            }
        }

        // Fields the template lacks go after the nearest earlier field in template order (F-TPL-7).
        var order = type.Fields.Select(f => f.Name).Concat(TypeDefinition.UniversalFields).ToList();
        int Rank(string key)
        {
            var i = order.IndexOf(key);
            return i < 0 ? int.MaxValue : i;
        }

        var values = given.ToDictionary(g => g.Key, g => g.Value, StringComparer.Ordinal);
        foreach (var key in given.Select(g => g.Key).Where(k => children.All(c => c.Key != k)).OrderBy(Rank).ThenBy(k => given.ToList().FindIndex(g => g.Key == k)))
        {
            var rank = Rank(key);
            var at = children.Count;
            if (rank != int.MaxValue)
            {
                var before = children.FindLastIndex(c => Rank(c.Key) < rank);
                at = before >= 0 ? before + 1 : children.FindIndex(c => Rank(c.Key) != int.MaxValue) is var first and >= 0 ? first : children.Count;
            }

            children.Insert(at, (key, null));
        }

        var pad = new string(' ', indent);
        var sb = new StringBuilder("fields:\n");
        foreach (var (key, raw) in children)
        {
            if (values.TryGetValue(key, out var value))
            {
                sb.Append(pad).Append(Render(key, value, indent)).Append('\n');
            }
            else if (raw is not null)
            {
                sb.Append(raw.StartsWith(pad, StringComparison.Ordinal) ? raw : pad + raw);
            }
        }

        return sb.ToString();
    }

    /// <summary><c>key: value</c> for a new value, at <paramref name="indent"/>: a list of mappings, such as a temporal list, gets a <c>- { ... }</c> line per item (format.md §7.3).</summary>
    private static string Render(string key, YamlValue value, int indent)
    {
        switch (value)
        {
            case YamlScalarValue scalar:
                var token = scalar.Render(null);
                return token.Length == 0 ? key + ":" : key + ": " + token;
            case YamlSequenceValue { Items.Count: > 0 } list when list.Items.All(i => i is YamlMappingValue):
                var pad = new string(' ', indent + 2);
                return key + ":" + string.Concat(list.Items.Select(i => "\n" + pad + "- " + i.RenderFlow(null, null)));
            default:
                return key + ": " + value.RenderFlow(null, null);
        }
    }

    /// <summary>A value's text as written, on one line, for a value inside a flow mapping.</summary>
    private static string ValueText(Text.SourceText source, YamlNode value) =>
        value.ContentEnd > value.Start ? source.Text[value.Start..value.ContentEnd] : "";

    /// <summary>Checks that the new file says what it was built to say (F-TPL-6: <c>name</c> reads back as exactly the name).</summary>
    private static void Verify(MarkdownFile template, MarkdownFile file, TypeDefinition type, string id, NewEntityRequest request, IReadOnlyList<KeyValuePair<string, YamlValue>> fields)
    {
        var fm = file.Frontmatter;
        var tfm = template.Frontmatter;
        var ok = fm is not null
            && YamlScalarValue.String(id).MatchesAsWritten(fm["id"])
            && YamlScalarValue.String(type.Key).MatchesAsWritten(fm["type"])
            && YamlScalarValue.String(request.Name).MatchesAsWritten(fm["name"])
            && fm["status"] is YamlScalar { IsNull: false }
            && (request.Status is null || YamlScalarValue.String(request.Status).MatchesAsWritten(fm["status"]))
            && (request.From is null || YamlScalarValue.String(request.From).MatchesAsWritten(fm["from"]))
            && (request.Aliases is null || (fm["aliases"] is YamlSequence a && a.Items.Count == request.Aliases.Count
                && request.Aliases.Zip(a.Items).All(p => YamlScalarValue.String(p.First).MatchesAsWritten(p.Second))))
            && fields.All(f => f.Value.MatchesAsWritten((fm["fields"] as YamlMapping)?[f.Key]));
        if (ok && tfm is not null)
        {
            // The template's values the caller didn't replace are copied as they are.
            foreach (var entry in tfm.Entries.Where(e => e.KeyText is { } k && k is not ("id" or "type" or "name")))
            {
                var key = entry.KeyText!;
                if (key == "fields" && entry.Value is YamlMapping templateFields)
                {
                    ok &= templateFields.Entries.Where(e => e.KeyText is { } f && fields.All(g => g.Key != f))
                        .All(e => YamlTreeCheck.Same(e.Value, (fm!["fields"] as YamlMapping)?[e.KeyText!]));
                }
                else if (!(key == "aliases" && request.Aliases is not null) && !(key == "status" && request.Status is not null)
                    && !(key == "from" && request.From is not null) && !(key == "status" && entry.Value is YamlScalar { IsNull: true }))
                {
                    ok &= YamlTreeCheck.Same(entry.Value, fm![key]);
                }
            }
        }

        if (!ok || file.Body != template.Body.Replace("{{name}}", request.Name, StringComparison.Ordinal).ReplaceLineEndings("\n"))
        {
            throw new EditVerificationException("The new entity didn't read back as built from its template, so it wasn't made.");
        }
    }

    /// <summary>Cuts <paramref name="slug"/> to <paramref name="max"/> characters, at a word boundary where there is one, without a trailing <c>-</c>.</summary>
    private static string Cut(string slug, int max)
    {
        if (slug.Length <= max)
        {
            return slug.TrimEnd('-');
        }

        var cut = slug[..max];
        if (slug[max] != '-' && cut.LastIndexOf('-') is var dash and > 0)
        {
            cut = cut[..dash];
        }

        return cut.TrimEnd('-');
    }
}
