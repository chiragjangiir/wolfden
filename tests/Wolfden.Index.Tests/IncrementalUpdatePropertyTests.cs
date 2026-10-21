using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wolfden.Core.Structure;
using static Wolfden.Index.Tests.IncrementalUpdateTests;

namespace Wolfden.Index.Tests;

/// <summary>
/// The property test of the file watcher's incremental update (#14): random
/// sequences of edits, new files, deletes, renames, folder moves, outline
/// changes, links to notes that are gone (where the OS allows file links,
/// #112), and the odd structural change to generated dens, applied as
/// synthetic batches. After every batch the index must have exactly the
/// content of a full rebuild of the same files. Seeds are fixed, so failures
/// reproduce.
/// </summary>
public sealed partial class IncrementalUpdatePropertyTests(ITestOutputHelper output)
{
    private const int Steps = 24;

    public static TheoryData<int> Seeds => [1, 2, 3, 5, 8, 13];

    [Theory]
    [MemberData(nameof(Seeds))]
    [Trait("Scenario", "SC-WATCH-1")]
    [Trait("Scenario", "SC-WATCH-2")]
    [Trait("Scenario", "SC-WATCH-7")]
    public void AnyChangeSequenceGivesTheSameIndexAsAFullRebuild(int seed)
    {
        var (files, _) = DenGenerator.Generate(seed);
        using var den = TempDen.FromFiles(files);
        var updater = new IndexUpdater(den.Root);
        updater.Rebuild("start");
        var editor = new DenEditor(den.Root, new Random(seed));
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var step = 0; step < Steps; step++)
        {
            var batch = editor.Batch();
            var update = updater.Apply(batch, out var retry);
            Assert.Empty(retry);
            var kind = update?.Kind.ToString() ?? "none";
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

            AssertSameAsFullRebuild(den, $"seed {seed}, step {step} ({string.Join(", ", editor.LastOperations)}): {kind}");
        }

        output.WriteLine(string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}")));
        Assert.True(kinds.GetValueOrDefault("Updated") > kinds.GetValueOrDefault("Rebuilt"), "Most batches should update incrementally.");
    }

    /// <summary>Makes random changes to a den's files and reports them as file events would.</summary>
    private sealed class DenEditor
    {
        private static readonly string[] Syllables = ["ar", "bel", "cor", "dra", "en", "fal", "gor", "hal", "is", "jan", "kel", "lor", "mar", "nev", "or", "pel"];
        private static readonly string[] Types = ["character", "character", "beacon", "location", "thread", "gizmo"];
        private static readonly string[] Statuses = ["canon", "canon", "draft", "idea", "retconned", "candidate"];

        private readonly string _root;
        private readonly Random _random;
        private readonly List<string> _targets = ["nobody-at-all", "Nobody Known"];
        private readonly List<string> _ids = [];
        private readonly List<(WatcherChangeTypes Kind, string Path, string? OldPath)> _events = [];
        private DenStructure _structure;
        private int _keys;

        public DenEditor(string root, Random random)
        {
            _root = root;
            _random = random;
            _structure = DenStructure.Load(root);
            foreach (var file in EntityFiles())
            {
                var text = File.ReadAllText(Full(_root, file));
                _targets.Add(Path.GetFileNameWithoutExtension(file));
                if (NameLine().Match(text) is { Success: true } name)
                {
                    _targets.Add(name.Groups[1].Value.Trim());
                }
            }
        }

        public List<string> LastOperations { get; } = [];

        /// <summary>One to three random changes, written to disk, as the batch a watcher would collect.</summary>
        public ChangeBatch Batch()
        {
            _events.Clear();
            LastOperations.Clear();
            for (var i = _random.Next(1, 4); i > 0; i--)
            {
                LastOperations.Add(Change());
            }

            var batcher = new ChangeBatcher(_root, TimeSpan.Zero, TimeSpan.Zero);
            var now = DateTime.UtcNow;
            foreach (var (kind, path, old) in _events)
            {
                batcher.Add(kind, Full(_root, path), old is null ? null : Full(_root, old), now);
            }

            return batcher.Take(now) ?? new ChangeBatch(new HashSet<string>(), new HashSet<string>(), false);
        }

        private string Change()
        {
            var entities = EntityFiles();
            var roll = _random.Next(100);
            switch (roll)
            {
                case < 30 when entities.Count > 0:
                {
                    var path = Pick(entities);
                    Write(path, Entity(path, keep: _random.Next(2) == 0 ? File.ReadAllText(Full(_root, path)) : null));
                    return $"edit {path}";
                }

                case < 45:
                {
                    var path = NewEntityPath();
                    Write(path, Entity(path, keep: null));
                    return $"add {path}";
                }

                case < 53 when entities.Count > 0:
                {
                    var path = Pick(entities);
                    File.Delete(Full(_root, path));
                    _events.Add((WatcherChangeTypes.Deleted, path, null));
                    return $"delete {path}";
                }

                case < 63 when entities.Count > 0:
                {
                    var path = Pick(entities);
                    var to = _random.Next(2) == 0 ? $"{path[..path.LastIndexOf('/')]}/{Key()}.md" : NewEntityPath();
                    EnsureFolder(to[..to.LastIndexOf('/')]);
                    File.Move(Full(_root, path), Full(_root, to));
                    _events.Add((WatcherChangeTypes.Renamed, to, path));
                    return $"move {path} to {to}";
                }

                case < 67:
                {
                    var folders = Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                        .Select(d => Path.GetRelativePath(_root, d).Replace('\\', '/'))
                        .Where(d => !d.StartsWith('.') && Regex.IsMatch(d, "/entities(/[^/.][^/]*)?$"))
                        .Order(StringComparer.Ordinal)
                        .ToList();
                    if (folders.Count == 0)
                    {
                        return "nothing";
                    }

                    var folder = Pick(folders);
                    var to = folder.EndsWith("/entities", StringComparison.Ordinal) ? $"{folder}/{Key()}" : $"{folder[..folder.LastIndexOf('/')]}/{Key()}";
                    if (folder.EndsWith("/entities", StringComparison.Ordinal))
                    {
                        // Move the whole entities folder down one level, inside itself.
                        var temp = $"{folder[..folder.LastIndexOf('/')]}/tmp{Key()}";
                        Directory.Move(Full(_root, folder), Full(_root, temp));
                        Directory.CreateDirectory(Full(_root, folder));
                        Directory.Move(Full(_root, temp), Full(_root, to));
                        _events.Add((WatcherChangeTypes.Renamed, temp, folder));
                        _events.Add((WatcherChangeTypes.Created, folder, null));
                        _events.Add((WatcherChangeTypes.Renamed, to, temp));
                    }
                    else
                    {
                        Directory.Move(Full(_root, folder), Full(_root, to));
                        _events.Add((WatcherChangeTypes.Renamed, to, folder));
                    }

                    return $"move folder {folder} to {to}";
                }

                case < 76:
                {
                    var books = _structure.Universes.SelectMany(u => u.Books).ToList();
                    if (books.Count == 0)
                    {
                        return "nothing";
                    }

                    var book = Pick(books);
                    var path = $"{book.Path}/outline.md";
                    if (File.Exists(Full(_root, path)) && _random.Next(4) == 0)
                    {
                        File.Delete(Full(_root, path));
                        _events.Add((WatcherChangeTypes.Deleted, path, null));
                        return $"delete {path}";
                    }

                    Write(path, Outline(book));
                    return $"outline {path}";
                }

                case < 82:
                {
                    var path = _random.Next(3) switch
                    {
                        0 => $"notes/{Key()}.md",
                        1 => $"{Pick(Containers()).Path}/entities/{Key()}.txt",
                        _ => $"{Pick(Containers()).Path}/entities/.{Key()}.md",
                    };
                    Write(path, "Loose notes, not an entity.\n");
                    return $"other {path}";
                }

                case < 87 when entities.Count > 0:
                {
                    // An editor's atomic save: write a temporary file, then rename it over the note.
                    var path = Pick(entities);
                    var temp = $"{path}.tmp{_random.Next(1000)}";
                    File.WriteAllText(Full(_root, temp), Entity(path, keep: File.ReadAllText(Full(_root, path))), new UTF8Encoding(false));
                    _events.Add((WatcherChangeTypes.Created, temp, null));
                    File.Move(Full(_root, temp), Full(_root, path), overwrite: true);
                    _events.Add((WatcherChangeTypes.Renamed, path, temp));
                    return $"atomic save {path}";
                }

                case < 90:
                {
                    var folder = $"{Pick(Containers()).Path}/entities/{Key()}";
                    EnsureFolder(folder);
                    return $"empty folder {folder}";
                }

                case < 93:
                {
                    // Structural: the den's settings or a book's chapters.
                    if (_random.Next(2) == 0)
                    {
                        var text = File.ReadAllText(Full(_root, "den.yaml"));
                        Write("den.yaml", text.Contains("spoiler_checks", StringComparison.Ordinal)
                            ? text.Replace("spoiler_checks: true\n", "", StringComparison.Ordinal)
                            : text + "spoiler_checks: true\n");
                        _structure = DenStructure.Load(_root);
                        return "den.yaml";
                    }

                    var books = _structure.Universes.SelectMany(u => u.Books).ToList();
                    if (books.Count == 0)
                    {
                        return "nothing";
                    }

                    var book = Pick(books);
                    var bookFile = Full(_root, book.FilePath);
                    Write(book.FilePath, File.ReadAllText(bookFile) + $"\nNotes on {Name()}.\n");
                    _structure = DenStructure.Load(_root);
                    return book.FilePath;
                }

                case < 96:
                {
                    // Structural folders: an empty would-be container (F-LAY-4), or a
                    // book or series folder renamed or deleted, reported as Windows
                    // does, by the folder's own event only.
                    if (_structure.Universes.Count == 0)
                    {
                        return "nothing";
                    }

                    var universe = Pick(_structure.Universes);
                    var works = universe.Series.Cast<Container>().Concat(universe.Books).ToList();
                    string what;
                    switch (_random.Next(3))
                    {
                        case 0:
                        {
                            var folder = $"{universe.Path}/{Pick(["books", "series"])}/{Key()}";
                            EnsureFolder(folder);
                            what = $"empty container folder {folder}";
                            break;
                        }

                        case 1 when works.Count > 0:
                        {
                            var work = Pick(works);
                            var to = $"{work.Path[..work.Path.LastIndexOf('/')]}/{Key()}";
                            Directory.Move(Full(_root, work.Path), Full(_root, to));
                            _events.Add((WatcherChangeTypes.Renamed, to, work.Path));
                            what = $"rename container folder {work.Path} to {to}";
                            break;
                        }

                        case 2 when works.Count > 1:
                        {
                            var work = Pick(works);
                            Directory.Delete(Full(_root, work.Path), recursive: true);
                            _events.Add((WatcherChangeTypes.Deleted, work.Path, null));
                            what = $"delete container folder {work.Path}";
                            break;
                        }

                        default:
                            return "nothing";
                    }

                    _structure = DenStructure.Load(_root);
                    return what;
                }

                case < 98:
                {
                    // A link to a note that's gone, new or in a note's place, as
                    // when the note it pointed to moved away. It's absent (#112).
                    // The random draws are the same where links can't be made.
                    var replace = entities.Count > 0 && _random.Next(2) == 0;
                    var path = replace ? Pick(entities) : NewEntityPath();
                    var target = Path.Combine(_root, "nowhere", $"{Key()}.md");
                    if (!CanLinkFiles.Value)
                    {
                        return "nothing";
                    }

                    if (replace)
                    {
                        File.Delete(Full(_root, path));
                        _events.Add((WatcherChangeTypes.Deleted, path, null));
                    }
                    else
                    {
                        EnsureFolder(path[..path.LastIndexOf('/')]);
                    }

                    File.CreateSymbolicLink(Full(_root, path), target);
                    _events.Add((WatcherChangeTypes.Created, path, null));
                    return $"broken link {path}";
                }

                default:
                {
                    if (entities.Count == 0)
                    {
                        return "nothing";
                    }

                    // A small edit: a link or a section added at the end.
                    var path = Pick(entities);
                    var text = File.ReadAllText(Full(_root, path));
                    Write(path, text + (_random.Next(2) == 0 ? $"\nMeets [[{Pick(_targets)}]].\n" : $"\n## {Pick(["Overview", "Want", "Odd bit"])}\nWith [[{Pick(_targets)}]].\n"));
                    return $"append {path}";
                }
            }
        }

        private List<Container> Containers() =>
            _structure.Universes.SelectMany(u => new Container[] { u }.Concat(u.Series).Concat(u.Books)).ToList();

        /// <summary>True where the OS and user may make file links (not Windows without developer mode).</summary>
        private static readonly Lazy<bool> CanLinkFiles = new(() =>
        {
            var link = Path.Combine(Path.GetTempPath(), $"wolfden-link-probe-{Guid.NewGuid():N}");
            try
            {
                File.CreateSymbolicLink(link, link + "-target");
                File.Delete(link);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        });

        /// <summary>The entity files, leaving out links, which only the broken-link change makes.</summary>
        private List<string> EntityFiles() =>
            Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories)
                .Where(f => new FileInfo(f).LinkTarget is null)
                .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
                .Where(f => Wolfden.Core.Entities.EntityCatalog.EntityFileScope(_structure, f) is not null)
                .Order(StringComparer.Ordinal)
                .ToList();

        private string NewEntityPath()
        {
            var container = Pick(Containers());
            var folder = Pick(["characters", "locations", "beacons", "misc/deep"]);
            return $"{container.Path}/entities/{folder}/{Key()}.md";
        }

        private void Write(string path, string text)
        {
            var full = Full(_root, path);
            var existed = File.Exists(full);
            var slash = path.LastIndexOf('/');
            EnsureFolder(slash > 0 ? path[..slash] : "");
            File.WriteAllText(full, text, new UTF8Encoding(false));
            _events.Add((existed ? WatcherChangeTypes.Changed : WatcherChangeTypes.Created, path, null));
            if (_random.Next(3) == 0 && path.Contains('/', StringComparison.Ordinal))
            {
                // Windows also reports the folder whose contents changed.
                _events.Add((WatcherChangeTypes.Changed, path[..path.LastIndexOf('/')], null));
            }
        }

        private void EnsureFolder(string folder)
        {
            if (folder.Length == 0)
            {
                return;
            }

            var parts = folder.Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                var path = string.Join('/', parts[..i]);
                if (!Directory.Exists(Full(_root, path)))
                {
                    Directory.CreateDirectory(Full(_root, path));
                    _events.Add((WatcherChangeTypes.Created, path, null));
                }
            }
        }

        /// <summary>A random entity file. With <paramref name="keep"/>, keeps that file's id and name most of the time, as a small edit would.</summary>
        private string Entity(string path, string? keep)
        {
            var universe = _structure.FindScope(path)?.Universe;
            var text = new StringBuilder("---\n");
            var keptId = keep is null ? null : IdLine().Match(keep) is { Success: true } m ? m.Groups[1].Value : null;
            var id = _random.Next(10) switch
            {
                < 6 => keptId ?? NewId(),
                6 when _ids.Count > 0 => Pick(_ids),
                7 => null,
                _ => NewId(),
            };
            if (id is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"id: {id}\n");
            }

            var type = Pick(Types);
            text.Append(CultureInfo.InvariantCulture, $"type: {type}\n");
            var keptName = keep is null ? null : NameLine().Match(keep) is { Success: true } n ? n.Groups[1].Value.Trim() : null;
            var name = _random.Next(10) switch
            {
                < 5 when keptName is not null => keptName,
                < 7 when _targets.Count > 0 => Pick(_targets),
                _ => Name(),
            };
            text.Append(CultureInfo.InvariantCulture, $"name: {name}\n");
            _targets.Add(name);
            if (_random.Next(3) == 0)
            {
                var alias = _random.Next(2) == 0 ? Pick(_targets) : Name();
                text.Append(CultureInfo.InvariantCulture, $"aliases: [{alias}]\n");
                _targets.Add(alias);
            }

            if (_random.Next(4) > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"status: {Pick(Statuses)}\n");
            }

            if (_random.Next(3) == 0 && universe is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"from: \"{Position(universe)}\"\n");
            }

            if (_random.Next(20) == 0)
            {
                // An error: the file is left out (F-GEN-2), and links to it say why.
                text.Append("aliases: [unclosed\n");
            }

            if (type == "beacon" && _random.Next(2) == 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"fields:\n  keeper:\n    - {{ value: \"[[{Pick(_targets)}]]\" }}\n  mode: {Pick(["lit", "dim", "dark"])}\n");
            }
            else if (type == "character" && _random.Next(2) == 0 && universe is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"fields:\n  role:\n    - {{ value: \"knows [[{Pick(_targets)}]]\", from: \"{Position(universe)}\" }}\n    - {{ value: later, from: \"{Position(universe)}\" }}\n");
            }

            text.Append("---\n\n");
            text.Append(CultureInfo.InvariantCulture, $"{name} knows [[{Pick(_targets)}]] and [[{Pick(_targets)}|someone]].\n");
            for (var i = _random.Next(0, 4); i > 0; i--)
            {
                var heading = Pick(["Overview", "Appearance", "Want", "Change log", "Odd bit"]);
                text.Append(CultureInfo.InvariantCulture, $"\n## {heading}\n");
                if (_random.Next(3) == 0 && universe is not null)
                {
                    text.Append(CultureInfo.InvariantCulture, $"<!-- wolfden: from={Position(universe)} -->\n");
                }

                text.Append(CultureInfo.InvariantCulture, $"About [[{Pick(_targets)}#{Pick(["Overview", "Want", "Nothing"])}]].\n");
            }

            _targets.Add(Path.GetFileNameWithoutExtension(path));
            if (id is not null)
            {
                _ids.Add(id);
                _targets.Add(id);
            }

            return text.ToString();
        }

        private string Outline(Book book)
        {
            var text = new StringBuilder($"# {book.Name} outline\n\n| row | chapter | pov | beat |\n|---|---|---|---|\n");
            for (var i = 1; i <= _random.Next(1, 5); i++)
            {
                var chapter = book.Chapters.Count > 0 && _random.Next(3) > 0 ? book.Chapters[_random.Next(book.Chapters.Count)].Key : "";
                text.Append(CultureInfo.InvariantCulture, $"| {i} | {chapter} | [[{Pick(_targets)}]] | Something with [[{Pick(_targets)}]]. |\n");
            }

            return text.ToString();
        }

        private string Position(Universe universe)
        {
            var books = universe.Books;
            if (books.Count == 0 || _random.Next(10) == 0)
            {
                return "nowhere:1";
            }

            var book = books[_random.Next(books.Count)];
            return book.Chapters.Count == 0 || _random.Next(4) == 0 ? book.Key : $"{book.Key}:{book.Chapters[_random.Next(book.Chapters.Count)].Key}";
        }

        private string NewId() => Ids.New(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + _random.Next()), Bytes(10));

        private byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            _random.NextBytes(bytes);
            return bytes;
        }

        private string Key() => Pick(Syllables) + Pick(Syllables) + (++_keys).ToString(CultureInfo.InvariantCulture);

        private string Name()
        {
            static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
            return $"{Cap(Pick(Syllables) + Pick(Syllables))} {Cap(Pick(Syllables) + Pick(Syllables))}";
        }

        private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];
    }

    [GeneratedRegex("^name: (.*)$", RegexOptions.Multiline)]
    private static partial Regex NameLine();

    [GeneratedRegex("^id: ([0-9A-Z]{26})\\s*$", RegexOptions.Multiline)]
    private static partial Regex IdLine();
}
