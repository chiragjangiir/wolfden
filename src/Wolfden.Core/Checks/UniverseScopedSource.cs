using Wolfden.Core.Structure;

namespace Wolfden.Core.Checks;

/// <summary>
/// A den as a check of one universe sees it (maintainer decision,
/// 2026-10-23): everything at the den level (<c>den.yaml</c>, packs, files at
/// the root), all of the chosen universe, and of every other universe only
/// its structure: its <c>universe.md</c>, its series' <c>series.md</c>, and
/// its books' <c>book.md</c>, so story order and positions resolve as they
/// do in the whole den (F-POS-5 still says a position names another
/// universe's book). The other universes' notes and outlines aren't listed or
/// read at all.
/// </summary>
internal sealed class UniverseScopedSource : IDenSource
{
    private readonly IDenSource _inner;
    private readonly string _universe;

    public UniverseScopedSource(IDenSource inner, string universeKey)
    {
        _inner = inner;
        _universe = universeKey;
    }

    public bool FileExists(string path) => Allowed(path, isFile: true) && _inner.FileExists(path);

    public byte[] ReadFile(string path) =>
        Allowed(path, isFile: true) ? _inner.ReadFile(path) : throw new FileNotFoundException($"'{path}' is outside the universe being checked.", path);

    public IEnumerable<string> GetDirectories(string path) =>
        _inner.GetDirectories(path).Where(d => Allowed(Join(path, d), isFile: false)).ToList();

    public IEnumerable<string> GetFiles(string path) =>
        _inner.GetFiles(path).Where(f => Allowed(Join(path, f), isFile: true)).ToList();

    public bool FolderExists(string path) => Allowed(path, isFile: false) && _inner.FolderExists(path);

    public IEnumerable<string> GetFolderLinks(string path) =>
        _inner.GetFolderLinks(path).Where(d => Allowed(Join(path, d), isFile: false)).ToList();

    /// <summary>True if the diagnostic's path is in another universe, so a check of this one leaves it out.</summary>
    public bool IsInOtherUniverse(string path)
    {
        var segments = path.Split('/');
        return segments.Length >= 2 && segments[0] == "universes" && segments[1] != _universe;
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    /// <summary>
    /// True for anything outside another universe, and inside one for the
    /// folders and container files of its structure:
    /// <c>[series/&lt;s&gt;/]books/&lt;b&gt;</c> with their <c>universe.md</c>,
    /// <c>series.md</c>, and <c>book.md</c>.
    /// </summary>
    private bool Allowed(string path, bool isFile)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != "universes" || segments[1] == _universe)
        {
            // The den level, the universes folder, a universe folder itself, or the chosen universe.
            return true;
        }

        var state = State.Universe;
        for (var i = 2; i < segments.Length; i++)
        {
            var token = segments[i];
            if (i == segments.Length - 1 && isFile)
            {
                return (state, token) is (State.Universe, "universe.md") or (State.Series, "series.md") or (State.Book, "book.md");
            }

            state = (state, token) switch
            {
                (State.Universe, "series") => State.SeriesList,
                (State.Universe, "books") => State.BookList,
                (State.SeriesList, _) => State.Series,
                (State.Series, "books") => State.BookList,
                (State.BookList, _) => State.Book,
                _ => State.None,
            };
            if (state == State.None)
            {
                return false;
            }
        }

        return !isFile;
    }

    private enum State
    {
        None,
        Universe,
        SeriesList,
        Series,
        BookList,
        Book,
    }
}
