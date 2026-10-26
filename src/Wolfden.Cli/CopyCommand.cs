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
          --to <scope>        where the copy goes: '<universe>' for universe scope,
                              or '<universe>/<key>' for a series or book of it
                              ('<universe>/series:<key>' or '<universe>/book:<key>'
                              if a series and a book share the key); by default,
                              beside the original. Never into a final book.
          --universe <key>    the original's universe, when the den has several
          --slug <slug>       the copy's file name without .md; by default, made
                              from the new name
          --allow-relinks     make the copy even if links that reach another
                              entity would reach the copy instead (they're listed)
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
            result = new DenTools(root).CopyEntity(
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
    /// Reads <c>--to</c>: <c>&lt;universe&gt;</c>, or <c>&lt;universe&gt;/&lt;key&gt;</c>
    /// for a series or book of it, or with <c>series:</c> or <c>book:</c>
    /// before the key to say which. A bare key that's a series or book of a
    /// universe, not a universe, gets a hint.
    /// </summary>
    private static bool TryScope(string root, string to, out string? universe, out string? series, out string? book, out string problem)
    {
        universe = series = book = null;
        problem = "";
        var slash = to.IndexOf('/', StringComparison.Ordinal);
        var u = (slash < 0 ? to : to[..slash]).Trim();
        var rest = slash < 0 ? null : to[(slash + 1)..].Trim();
        var structure = DenStructure.Load(root);
        var found = structure.FindUniverse(u);
        if (found is null)
        {
            var holders = structure.Universes.Where(x => x.Series.Any(s => s.Key == u) || x.FindBook(u) is not null).Select(x => $"'{x.Key}/{u}'").ToList();
            problem = $"the den has no universe '{u}', so --to '{to}' doesn't name a scope." +
                (holders.Count > 0 ? $" To copy into the series or book '{u}', give --to {string.Join(" or ", holders)}." : "") +
                " Give --to '<universe>' or '<universe>/<series or book>'.";
            return false;
        }

        universe = found.Key;
        if (string.IsNullOrEmpty(rest))
        {
            return true;
        }

        if (rest.StartsWith("series:", StringComparison.Ordinal))
        {
            series = rest["series:".Length..];
            return true;
        }

        if (rest.StartsWith("book:", StringComparison.Ordinal))
        {
            book = rest["book:".Length..];
            return true;
        }

        var isSeries = found.Series.Any(s => s.Key == rest);
        var isBook = found.FindBook(rest) is not null;
        if (isSeries && isBook)
        {
            problem = $"universe '{u}' has a series and a book named '{rest}': give --to '{u}/series:{rest}' or '{u}/book:{rest}'.";
            return false;
        }

        if (isSeries)
        {
            series = rest;
        }
        else
        {
            // A key that's neither: the copy reports the unknown book, with the books it has.
            book = rest;
        }

        return true;
    }
}
