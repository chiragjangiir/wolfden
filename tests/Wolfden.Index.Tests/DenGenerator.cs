using System.Globalization;
using System.Text;

namespace Wolfden.Index.Tests;

/// <summary>
/// Writes a random but deterministic den for a seed: invented names only,
/// two universes, two series in the first and one in the second (and
/// sometimes one with no books), standalone books, and entities
/// at every scope. The entities mix what canon views have to get right:
/// every status, entity <c>from</c>s that resolve and don't, plain values,
/// single entries and temporal lists with gaps, overlaps, nulls, empty
/// ranges and pins, fields with and without type defaults, standard
/// sections that hand over, subsections, bookkeeping sections, and a
/// thread's Payoff. It aims for files that load: what it writes may warn,
/// but shouldn't be an error.
/// </summary>
internal sealed class DenGenerator
{
    private static readonly string[] Syllables =
        ["ar", "bel", "cor", "dra", "en", "fal", "gor", "hal", "is", "jan", "kel", "lor", "mar", "nev", "or", "pel", "quin", "ros", "sel", "tor", "ul", "ven", "wyn", "zar"];

    private static readonly string[] Statuses = ["canon", "canon", "canon", "draft", "draft", "idea", "retconned", "candidate"];

    private static readonly string[] CharacterSections = ["Overview", "Appearance", "Backstory", "Want", "Arc"];

    private static readonly string[] BeaconSections = ["Overview", "Signal"];

    private static readonly string[] Modes = ["lit", "dim", "dark"];

    private readonly Random _random;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private List<(string Book, List<string> Chapters)> _books = [];

    private DenGenerator(int seed)
    {
        _random = new Random(seed);
    }

    /// <summary>The number of entity files written.</summary>
    public int EntityCount { get; private set; }

    /// <summary>The files of the den for <paramref name="seed"/>, by den-relative path.</summary>
    public static (Dictionary<string, string> Files, int EntityCount) Generate(int seed)
    {
        var generator = new DenGenerator(seed);
        generator.Run(seed);
        return (generator._files, generator.EntityCount);
    }

    private void Run(int seed)
    {
        _files["den.yaml"] = $"""
            format: 1
            name: Generated {seed}
            packs: [beacons]
            bookkeeping_sections: ["Scratch *"]

            """;
        _files["packs/beacons/pack.yaml"] = "name: Beacons\ndescription: An invented pack for the generated dens.\n";
        _files["packs/beacons/types/beacon.yaml"] = """
            name: Beacon
            plural: beacons
            fields:
              mode:
                type: enum
                values: [lit, dim, dark]
                default: lit
                temporal: true
              label: { type: string, default: unnamed }
              keeper: { type: ref, to: character, temporal: true }
              range: string
            sections:
              - Overview
              - Signal

            """;

        Universe(Key(), series: 2);
        Universe(Key(), series: 1);
    }

    /// <summary>
    /// A universe with <paramref name="series"/> series of 1 to 3 books
    /// each, 0 to 2 standalone books between them, and maybe an empty series
    /// at the end.
    /// </summary>
    private void Universe(string key, int series)
    {
        var root = $"universes/{key}";
        var order = new List<string>();
        var works = new List<string>();
        _books = [];

        for (var s = 0; s < series; s++)
        {
            var seriesKey = Key();
            order.Add(seriesKey);
            var seriesPath = $"{root}/series/{seriesKey}";
            works.Add(seriesPath);
            var books = new List<string>();
            for (var i = _random.Next(1, 4); i > 0; i--)
            {
                books.Add(Book($"{seriesPath}/books", works));
            }

            _files[$"{seriesPath}/series.md"] = $"---\nname: {Name()}\nbooks: [{string.Join(", ", books)}]\n---\n";
            for (var i = _random.Next(0, 3); i > 0; i--)
            {
                order.Add(Book($"{root}/books", works));
            }
        }

        if (_random.Next(2) == 0)
        {
            var empty = Key();
            order.Add(empty);
            _files[$"{root}/series/{empty}/series.md"] = $"---\nname: {Name()}\n---\n\nNo books yet.\n";
            works.Add($"{root}/series/{empty}");
        }

        _files[$"{root}/universe.md"] = $"---\nname: {Name()}\norder: [{string.Join(", ", order)}]\n---\n\nAn invented universe.\n";

        Entities(root, _random.Next(3, 7));
        foreach (var path in works)
        {
            Entities(path, _random.Next(1, 5));
        }
    }

    /// <summary>Writes a book under <paramref name="parent"/> and returns its key.</summary>
    private string Book(string parent, List<string> works)
    {
        var book = Key();
        var chapters = Chapters();
        _books.Add((book, chapters));
        var path = $"{parent}/{book}";
        _files[$"{path}/book.md"] = $"---\nname: {Name()}\nstatus: draft\nchapters: [{string.Join(", ", chapters)}]\n---\n";
        works.Add(path);
        return book;
    }

    private List<string> Chapters()
    {
        var count = _random.Next(0, 6);
        var chapters = new List<string>();
        if (count > 0 && _random.Next(3) == 0)
        {
            chapters.Add("prologue");
        }

        for (var i = 1; chapters.Count < count; i++)
        {
            chapters.Add(i.ToString(CultureInfo.InvariantCulture));
            if (chapters.Count < count && _random.Next(4) == 0)
            {
                chapters.Add($"{i}b");
            }
        }

        return chapters;
    }

    private void Entities(string scopePath, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var kind = _random.Next(10) switch
            {
                < 4 => "character",
                < 7 => "beacon",
                < 8 => "thread",
                < 9 => "location",
                _ => "gizmo", // not an enabled type, so the entity is untyped
            };
            var name = Name();
            var slug = Key();
            var folder = kind switch { "character" => "characters", "beacon" => "beacons", "thread" => "threads", "location" => "locations", _ => "gizmos" };
            _files[$"{scopePath}/entities/{folder}/{slug}.md"] = Entity(kind, name);
            EntityCount++;
        }
    }

    private string Entity(string type, string name)
    {
        var text = new StringBuilder();
        text.Append("---\n");
        text.Append(CultureInfo.InvariantCulture, $"type: {type}\nname: {name}\n");
        if (_random.Next(6) > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"status: {Pick(Statuses)}\n");
        }

        if (_random.Next(3) > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"from: \"{Position(unresolvedOneIn: 12)}\"\n");
        }

        var fields = Fields(type);
        if (fields.Count > 0)
        {
            text.Append("fields:\n");
            foreach (var line in fields)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {line}\n");
            }
        }

        text.Append("---\n\n");
        if (_random.Next(2) == 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"# {name}\n\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"{Sentence()}\n");
        var standard = type switch { "character" => CharacterSections, "beacon" => BeaconSections, "thread" => ["Overview", "Setup", "Payoff"], _ => ["Overview"] };
        var sections = _random.Next(1, 6);
        for (var i = 0; i < sections; i++)
        {
            var heading = _random.Next(8) switch
            {
                0 => "Change log",
                1 => "Scratch notes",
                2 => "Odd custom bit",
                _ => Pick(standard),
            };
            Section(text, 2, heading);
            var subsections = _random.Next(3) == 0 ? _random.Next(1, 3) : 0;
            for (var j = 0; j < subsections; j++)
            {
                Section(text, 3, _random.Next(5) == 0 ? "Session notes" : $"{Pick(Syllables)} detail");
            }
        }

        return text.ToString();
    }

    private void Section(StringBuilder text, int level, string heading)
    {
        text.Append(CultureInfo.InvariantCulture, $"\n{new string('#', level)} {heading}\n");
        var attributes = new List<string>();
        if (_random.Next(2) == 0)
        {
            attributes.Add($"from={Position(unresolvedOneIn: 15)}");
        }

        if (_random.Next(5) == 0)
        {
            attributes.Add($"until={Position(unresolvedOneIn: 15)}");
        }

        if (_random.Next(6) == 0)
        {
            attributes.Add($"status={Pick(Statuses)}");
        }

        if (_random.Next(6) == 0)
        {
            attributes.Add("pin=true");
        }

        if (_random.Next(10) == 0)
        {
            attributes.Add(_random.Next(2) == 0 ? "canon=false" : "canon=true");
        }

        if (attributes.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"<!-- wolfden: {string.Join(' ', attributes)} -->\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"{Sentence()}\n");
    }

    private List<string> Fields(string type)
    {
        var fields = new List<string>();
        switch (type)
        {
            case "character":
                Maybe(fields, "role", () => Temporal(() => Quote(Sentence())));
                Maybe(fields, "gender", () => Quote(Pick(["woman", "man", "neither"])));
                Maybe(fields, "pronouns", () => Single(() => Quote(Pick(["she/her", "he/him", "they/them"]))));
                Maybe(fields, "died", () => Single(() => $"{{ text: \"{_random.Next(1, 28)} Frost\" }}"));
                Maybe(fields, "skills", () => $"[{Quote(Sentence())}, {Quote(Sentence())}]");
                break;
            case "beacon":
                Maybe(fields, "mode", () => _random.Next(3) == 0 ? Pick(Modes) : Temporal(() => Pick(Modes)));
                Maybe(fields, "label", () => _random.Next(4) == 0 ? "~" : Quote(Name()));
                Maybe(fields, "range", () => Quote($"{_random.Next(2, 90)} leagues"));
                break;
            case "thread":
                Maybe(fields, "planted", () => $"[\"{Position(unresolvedOneIn: 10)}\"]");
                Maybe(fields, "paid_off", () => $"\"{Position(unresolvedOneIn: 10)}\"");
                break;
            default:
                Maybe(fields, "colour", () => Temporal(() => Quote(Pick(["red", "green", "blue"]))));
                break;
        }

        if (_random.Next(5) == 0)
        {
            fields.Add("placeholder: true");
        }

        return fields;
    }

    private void Maybe(List<string> fields, string name, Func<string> value)
    {
        if (_random.Next(4) > 0)
        {
            var text = value();
            fields.Add(text.StartsWith('\n') ? $"{name}:{text.Replace("\n", "\n    ", StringComparison.Ordinal)}" : $"{name}: {text}");
        }
    }

    /// <summary>A single entry, in flow style.</summary>
    private string Single(Func<string> value) => _random.Next(3) == 0 ? value() : Entry(value, allowUntil: false);

    /// <summary>A temporal list in block style: entries in a random order, with gaps, overlaps, nulls, and statuses.</summary>
    private string Temporal(Func<string> value)
    {
        var count = _random.Next(1, 5);
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- {Entry(value, allowUntil: true)}");
        }

        return text.ToString();
    }

    private string Entry(Func<string> value, bool allowUntil)
    {
        var parts = new List<string> { $"value: {(_random.Next(8) == 0 ? "~" : value())}" };
        if (_random.Next(5) > 0)
        {
            parts.Add($"from: \"{Position(unresolvedOneIn: 15)}\"");
        }

        if (allowUntil && _random.Next(4) == 0)
        {
            parts.Add($"until: \"{Position(unresolvedOneIn: 15)}\"");
        }

        if (_random.Next(5) == 0)
        {
            parts.Add($"status: {Pick(Statuses)}");
        }

        if (_random.Next(5) == 0)
        {
            parts.Add("pin: true");
        }

        return $"{{ {string.Join(", ", parts)} }}";
    }

    /// <summary>A position in the current universe: a chapter or a bare book, or now and then one that doesn't resolve.</summary>
    private string Position(int unresolvedOneIn)
    {
        if (_random.Next(unresolvedOneIn) == 0)
        {
            return _random.Next(2) == 0 ? "nowhere:1" : $"{Pick(_books).Book}:99";
        }

        var (book, chapters) = Pick(_books);
        return chapters.Count == 0 || _random.Next(5) == 0 ? book : $"{book}:{Pick(chapters)}";
    }

    private string Key()
    {
        while (true)
        {
            var key = Pick(Syllables) + Pick(Syllables) + (_random.Next(3) == 0 ? Pick(Syllables) : "");
            if (_keys.Add(key))
            {
                return key;
            }
        }
    }

    private string Name()
    {
        static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
        return $"{Cap(Pick(Syllables) + Pick(Syllables))} {Cap(Pick(Syllables) + Pick(Syllables))}";
    }

    private string Sentence() => $"The {Pick(Syllables)}{Pick(Syllables)} {Pick(["drifts", "burns", "waits", "turns", "falls"])} near {Name()}.";

    private static string Quote(string text) => $"\"{text}\"";

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];
}
