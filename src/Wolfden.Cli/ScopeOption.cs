using Wolfden.Core.Structure;

namespace Wolfden.Cli;

/// <summary>
/// An option that names where a note goes: <c>wolfden copy --to</c> (#129)
/// and <c>wolfden new --in</c> (#29). It takes <c>&lt;universe&gt;</c> (or
/// <c>&lt;universe&gt;/</c>), or <c>&lt;universe&gt;/&lt;key&gt;</c> for a
/// series or book of it, with <c>series:</c> or <c>book:</c> before the key
/// to say which. In a den with one universe, a bare series or book key is
/// enough (as for <c>check</c>, #127), unless it's also the universe's key.
/// </summary>
/// <param name="Option">The option, such as <c>--to</c>.</param>
/// <param name="Into">How a message says to put the note in a series or book: <c>copy into</c> or <c>create it in</c>.</param>
/// <param name="Done">The same as a past participle: <c>copied</c> or <c>created</c>.</param>
internal sealed record ScopeOption(string Option, string Into, string Done)
{
    /// <summary>Reads the option's value, <paramref name="value"/>, for the den at <paramref name="root"/>.</summary>
    public bool TryParse(string root, string value, out string? universe, out string? series, out string? book, out string problem)
    {
        universe = series = book = null;
        problem = "";
        var structure = DenStructure.Load(root);
        if (!structure.IsLoaded)
        {
            problem = $"den.yaml doesn't load, so {Option} can't be read and nothing can be {Done}. Run 'wolfden check' to see why.";
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return TryBareKey(structure, value.Trim(), out universe, out series, out book, out problem);
        }

        var u = value[..slash].Trim();
        var rest = value[(slash + 1)..].Trim();
        var found = structure.FindUniverse(u);
        if (found is null)
        {
            problem = $"the den has no universe '{u}', so {Option} '{value}' doesn't name a scope. Give {Option} '<universe>' or '<universe>/<series or book>'.";
            return false;
        }

        universe = found.Key;
        return rest.Length == 0 || TryWork(found, rest, out series, out book, out problem);
    }

    /// <summary>A value without <c>/</c>: a universe, or in a den with one universe, a series or book of it.</summary>
    private bool TryBareKey(DenStructure structure, string key, out string? universe, out string? series, out string? book, out string problem)
    {
        universe = series = book = null;
        problem = "";
        var only = structure.Universes.Count == 1 ? structure.Universes[0] : null;
        if (only is not null && (key.StartsWith("series:", StringComparison.Ordinal) || key.StartsWith("book:", StringComparison.Ordinal)))
        {
            universe = only.Key;
            return TryWork(only, key, out series, out book, out problem);
        }

        var asUniverse = structure.FindUniverse(key);
        var kinds = only is null ? [] : new[] { only.Series.Any(s => s.Key == key) ? "series" : null, only.FindBook(key) is not null ? "book" : null }.OfType<string>().ToList();
        var work = kinds.Count > 0 ? only : null;
        if (asUniverse is not null && work is not null)
        {
            var what = string.Join(" and a ", kinds.Select(k => k + " of it"));
            var spellings = string.Join(", or ", kinds.Select(k => $"'{work.Key}/{k}:{key}' for the {k}"));
            problem = $"'{key}' is the universe and a {what}: give {Option} '{key}/' for the universe, or {spellings}.";
            return false;
        }

        if (asUniverse is not null)
        {
            universe = asUniverse.Key;
            return true;
        }

        if (work is not null)
        {
            universe = work.Key;
            return TryWork(work, key, out series, out book, out problem);
        }

        var holders = structure.Universes.Where(x => x.Series.Any(s => s.Key == key) || x.FindBook(key) is not null).Select(x => $"'{x.Key}/{key}'").ToList();
        problem = $"the den has no universe '{key}'" + (only is null ? "" : $", and universe '{only.Key}' has no series or book '{key}'") +
            $", so {Option} '{key}' doesn't name a scope." +
            (holders.Count > 0 ? $" To {Into} the series or book '{key}', give {Option} {string.Join(" or ", holders)}." : "") +
            $" Give {Option} '<universe>' or '<universe>/<series or book>'.";
        return false;
    }

    /// <summary>A series or book key of <paramref name="universe"/>, with <c>series:</c> or <c>book:</c> before it to say which.</summary>
    private bool TryWork(Universe universe, string key, out string? series, out string? book, out string problem)
    {
        series = book = null;
        problem = "";
        if (key.StartsWith("series:", StringComparison.Ordinal))
        {
            series = key["series:".Length..].Trim();
            return true;
        }

        if (key.StartsWith("book:", StringComparison.Ordinal))
        {
            book = key["book:".Length..].Trim();
            return true;
        }

        var isSeries = universe.Series.Any(s => s.Key == key);
        if (isSeries && universe.FindBook(key) is not null)
        {
            problem = $"universe '{universe.Key}' has a series and a book named '{key}': give {Option} '{universe.Key}/series:{key}' or '{universe.Key}/book:{key}'.";
            return false;
        }

        if (isSeries)
        {
            series = key;
        }
        else
        {
            // A key that's neither: the write reports the unknown book, with the books it has.
            book = key;
        }

        return true;
    }
}
