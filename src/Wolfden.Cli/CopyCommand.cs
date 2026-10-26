using Microsoft.Data.Sqlite;
using Wolfden.Core.Structure;
using Wolfden.Mcp;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden copy &lt;entity&gt; &lt;new name&gt; [--to &lt;scope&gt;]</c>:
/// copies an entity to start a new one from it (#129), with a new id, so
/// the copy is never an F-ID-3 duplicate. It does what the MCP tool
/// <c>copy_entity</c> does (<see cref="DenTools.CopyEntity"/>): the copy
/// goes to the den's files only, and nothing is committed.
/// </summary>
/// <remarks>
/// Exit codes: 0 when the copy is made, and 2 when the command line is
/// wrong, there's no den, or the copy is refused (an unknown entity or
/// scope, a final book, a file name that's taken, or links the copy would
/// take from another entity without <c>--allow-relinks</c>).
/// </remarks>
internal static class CopyCommand
{
    internal static readonly CommandSpec Spec = new(
        "copy",
        "usage: wolfden copy <entity> <new name> [--to <scope>] [--universe <key>] [--slug <slug>] [--allow-relinks] [--den <den>] [--json]",
        """
        Copies an entity to start a new one from it, such as another character
        like this one. The copy gets a new id that no file in the den uses, the
        new name, a file name made from it (with -2, -3 if it's taken), and no
        aliases, which stay the original's. Its type, fields, sections, status,
        and from and until are the original's, and the links in it are left as
        they are. It's written to the den's files only; nothing is committed.

          <entity>            the entity to copy, named as a link would name it:
                              by name, alias, file name, or id; or by its path
          <new name>          the copy's name
          --to <scope>        where the copy goes: '<universe>' (or '<universe>/')
                              for universe scope, or '<universe>/<key>' for a
                              series or book of it ('<universe>/series:<key>' or
                              '<universe>/book:<key>' if a series and a book share
                              the key); in a den with one universe, a bare series
                              or book key will do, unless it's the universe's key
                              too. By default, beside the original. Never into a
                              final book.
          --universe <key>    the original's universe, when the den has several
          --slug <slug>       the copy's file name without .md; by default, made
                              from the new name
          --allow-relinks     make the copy even if links that reach an entity
                              would reach another or become ambiguous (they're
                              listed)
          --den <den>         the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --json              print one JSON document instead of text

        It prints where the copy went and what it means: links in it that break
        in another universe, where it enters canon, and links elsewhere that now
        resolve differently. Exits 0 when the copy is made, and 2 if it isn't.
        """,
        ["--json", "--allow-relinks"],
        ["--den", "--universe", "--to", "--slug"],
        [],
        2,
        "give the entity and the new name, and nothing else (quote a name with spaces).");

    /// <summary>The command's options and commands, in place of the MCP tool's parameters and tools, in its messages.</summary>
    internal static readonly ToolWords Words = new(
        "pass --allow-relinks", "as <new name>", "--slug", "with --universe", "use 'wolfden query --brief'", "run 'wolfden check'", "run 'wolfden check'",
        "run 'wolfden check'");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (line.Arguments.Count < 2)
        {
            error.WriteLine("wolfden copy: give the entity to copy and the copy's name.");
            error.WriteLine(Spec.Usage);
            return CliApp.ExitUsage;
        }

        if (DenLocation.Resolve(Spec.Name, line.Value("--den"), error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        string? toUniverse = null, toSeries = null, toBook = null;
        if (line.Value("--to") is { } to && !TryScope(root, to, out toUniverse, out toSeries, out toBook, out var problem))
        {
            error.WriteLine($"wolfden copy: {problem}");
            return CliApp.ExitUsage;
        }

        WriteResult result;
        try
        {
            result = new DenTools(root) { Words = Words }.CopyEntity(
                line.Arguments[0], line.Arguments[1], line.Value("--universe"), toUniverse, toSeries, toBook, line.Value("--slug"), line.Has("--allow-relinks"));
        }
        catch (DenToolException e)
        {
            error.WriteLine($"wolfden copy: {e.Message}");
            return CliApp.ExitUsage;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine($"wolfden copy: couldn't read the den at '{root}' or its index: {e.Message}");
            return CliApp.ExitUsage;
        }

        if (line.Has("--json"))
        {
            CliJson.Write(output, result);
            return CliApp.ExitOk;
        }

        output.WriteLine($"Copied to {result.Path}");
        foreach (var effect in result.Effects)
        {
            output.WriteLine($"  {effect}");
        }

        foreach (var w in result.Warnings)
        {
            output.WriteLine($"{w.Path}:{w.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}: {w.Severity} {w.Rule}: {w.Message}");
        }

        return CliApp.ExitOk;
    }

    /// <summary>
    /// Reads <c>--to</c>: <c>&lt;universe&gt;</c> (or <c>&lt;universe&gt;/</c>),
    /// or <c>&lt;universe&gt;/&lt;key&gt;</c> for a series or book of it, with
    /// <c>series:</c> or <c>book:</c> before the key to say which. In a den
    /// with one universe, a bare series or book key is enough (as for
    /// <c>check</c>, #127), unless it's also the universe's key.
    /// </summary>
    private static bool TryScope(string root, string to, out string? universe, out string? series, out string? book, out string problem)
    {
        universe = series = book = null;
        problem = "";
        var structure = DenStructure.Load(root);
        if (!structure.IsLoaded)
        {
            problem = "den.yaml doesn't load, so --to can't be read and nothing can be copied. Run 'wolfden check' to see why.";
            return false;
        }

        var slash = to.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return TryBareKey(structure, to.Trim(), out universe, out series, out book, out problem);
        }

        var u = to[..slash].Trim();
        var rest = to[(slash + 1)..].Trim();
        var found = structure.FindUniverse(u);
        if (found is null)
        {
            problem = $"the den has no universe '{u}', so --to '{to}' doesn't name a scope. Give --to '<universe>' or '<universe>/<series or book>'.";
            return false;
        }

        universe = found.Key;
        return rest.Length == 0 || TryWork(found, rest, out series, out book, out problem);
    }

    /// <summary>A <c>--to</c> without <c>/</c>: a universe, or in a den with one universe, a series or book of it.</summary>
    private static bool TryBareKey(DenStructure structure, string key, out string? universe, out string? series, out string? book, out string problem)
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
            problem = $"'{key}' is the universe and a {what}: give --to '{key}/' for the universe, or {spellings}.";
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
            $", so --to '{key}' doesn't name a scope." +
            (holders.Count > 0 ? $" To copy into the series or book '{key}', give --to {string.Join(" or ", holders)}." : "") +
            " Give --to '<universe>' or '<universe>/<series or book>'.";
        return false;
    }

    /// <summary>A series or book key of <paramref name="universe"/>, with <c>series:</c> or <c>book:</c> before it to say which.</summary>
    private static bool TryWork(Universe universe, string key, out string? series, out string? book, out string problem)
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
            problem = $"universe '{universe.Key}' has a series and a book named '{key}': give --to '{universe.Key}/series:{key}' or '{universe.Key}/book:{key}'.";
            return false;
        }

        if (isSeries)
        {
            series = key;
        }
        else
        {
            // A key that's neither: the copy reports the unknown book, with the books it has.
            book = key;
        }

        return true;
    }
}
