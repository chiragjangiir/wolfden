using Wolfden.Core.Documents;
using Wolfden.Core.Positions;
using Wolfden.Core.Types;

namespace Wolfden.Core.Structure;

/// <summary>A diagnostic in one file of a den.</summary>
/// <param name="Path">The file or folder, relative to the den root, with <c>/</c>.</param>
/// <param name="Diagnostic">The diagnostic.</param>
public sealed record DenDiagnostic(string Path, Diagnostic Diagnostic)
{
    /// <inheritdoc />
    public override string ToString() => $"{Path}:{Diagnostic}";
}

/// <summary>
/// A den's structure, read-only: <c>den.yaml</c>, its packs and types, its
/// universes, series, and books, the story order of the books, and each
/// book's chapters with their chapter fields (format.md §5, §6, §8.2;
/// types.md). It's what positions resolve against. It reads only
/// <c>den.yaml</c>, pack files, and container files, never entities, and
/// never writes (F-ID-6).
/// </summary>
/// <remarks>
/// Containers with errors are left out with everything under them (F-GEN-2).
/// <see cref="Diagnostics"/> holds the problems that decide the structure:
/// container files' parse errors, names and ids, a book's status and
/// chapters, <c>order</c> and <c>books</c>, key clashes, <c>den.yaml</c>'s
/// <c>packs</c> (F-DEN-4), pack files (<see cref="DenTypes.Diagnostics"/>),
/// and chapter-field values, which are ignored when they don't match their
/// definitions (types.md F-CHAP-3). <see cref="Checks.DenCheck"/> adds the
/// positions in container section comments (F-POS-4). Other checks of
/// container files, such as authors, aren't made yet.
/// </remarks>
public sealed class DenStructure
{
    private readonly List<Universe> _universes = [];
    private readonly List<DenDiagnostic> _diagnostics = [];

    private DenStructure()
    {
    }

    /// <summary>
    /// True if <c>den.yaml</c> loaded (F-DEN-1). If not, the den has no
    /// universes, and <see cref="Diagnostics"/> says why.
    /// </summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The den's format number from <c>den.yaml</c>, or null if it didn't load.</summary>
    public int? Format { get; private set; }

    /// <summary>
    /// True if <c>den.yaml</c> turns on the spoiler checks with
    /// <c>spoiler_checks: true</c> (F-DEN-7). In format 1 that's F-LINK-13.
    /// </summary>
    public bool SpoilerChecks { get; internal set; }

    /// <summary>
    /// The den's own bookkeeping patterns from <c>den.yaml</c>'s
    /// <c>bookkeeping_sections</c> (F-DEN-6), as written. The built-in ones
    /// (F-SEC-12) aren't included.
    /// </summary>
    public IReadOnlyList<string> BookkeepingSections { get; internal set; } = [];

    /// <summary>
    /// The den's enabled packs and types (types.md F-PACK-9). Until
    /// <c>den.yaml</c> loads, the built-in packs alone.
    /// </summary>
    public DenTypes Types { get; internal set; } = DenTypes.BuiltIn;

    /// <summary>The universes that loaded, by key in ASCII order.</summary>
    public IReadOnlyList<Universe> Universes => _universes;

    /// <summary>Problems found while loading the structure, by path and then line.</summary>
    public IReadOnlyList<DenDiagnostic> Diagnostics => _diagnostics;

    /// <summary>The highest den format this version of Wolfden reads (F-DEN-1).</summary>
    public const int SupportedFormat = 1;

    /// <summary>Loads the den whose root folder is <paramref name="root"/>.</summary>
    public static DenStructure Load(string root) => Load(new FileSystemDenSource(root));

    /// <summary>Loads a den's structure. Never throws for bad content: problems come back as <see cref="Diagnostics"/>.</summary>
    public static DenStructure Load(IDenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var den = new DenStructure();
        new DenLoader(den, source).Load();
        var sorted = den._diagnostics
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Diagnostic.Line ?? 0)
            .ToList();
        den._diagnostics.Clear();
        den._diagnostics.AddRange(sorted);
        return den;
    }

    /// <summary>The universe with key <paramref name="key"/>, or null.</summary>
    public Universe? FindUniverse(string key) => _universes.FirstOrDefault(u => u.Key == key);

    /// <summary>
    /// The container a file or folder belongs to: the nearest universe,
    /// series, or book folder above it (format.md F-LAY-4, F-LAY-5). Null if
    /// the path isn't in a universe, or is in a container that was left out
    /// for errors (F-GEN-2).
    /// </summary>
    /// <param name="path">A path relative to the den root, with <c>/</c> or <c>\</c>.</param>
    public Container? FindScope(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Paths are den-relative and already normal: no leading '/', and no '.' or '..'.
        var segments = path.Replace('\\', '/').Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
        {
            return null;
        }

        if (segments.Length < 2 || segments[0] != "universes" || FindUniverse(segments[1]) is not { } universe)
        {
            return null;
        }

        Container scope = universe;
        Series? series = null;
        var i = 2;
        if (segments.Length > i + 1 && segments[i] == "series")
        {
            series = universe.Series.FirstOrDefault(s => s.Key == segments[i + 1]);
            if (series is null)
            {
                return null;
            }

            scope = series;
            i += 2;
        }

        if (segments.Length > i + 1 && segments[i] == "books")
        {
            var books = series is not null ? series.Books : universe.Works.OfType<Book>();
            var book = books.FirstOrDefault(b => b.Key == segments[i + 1]);
            if (book is null)
            {
                return null;
            }

            scope = book;
        }

        return scope;
    }

    /// <summary>
    /// Resolves a position written in the file at <paramref name="path"/>,
    /// against that file's universe (F-POS-4, F-POS-5).
    /// </summary>
    /// <exception cref="ArgumentException">The path isn't in a loaded container.</exception>
    public PositionResolution Resolve(string path, StoryPosition position)
    {
        var scope = FindScope(path) ?? throw new ArgumentException($"'{path}' isn't in a loaded universe, series, or book.", nameof(path));
        return scope.Universe.Resolve(position);
    }

    internal void Add(string path, Diagnostic diagnostic) => _diagnostics.Add(new DenDiagnostic(path, diagnostic));

    internal void AddUniverse(Universe universe) => _universes.Add(universe);

    internal void SetLoaded(int format)
    {
        IsLoaded = true;
        Format = format;
    }
}
