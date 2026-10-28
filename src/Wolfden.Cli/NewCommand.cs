using Microsoft.Data.Sqlite;
using Wolfden.Mcp;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden new &lt;type&gt; &lt;name&gt; [--in &lt;scope&gt;]</c>: creates a
/// note from its type's template (#29; types.md F-TPL-6), with a new id. It
/// does what the MCP tool <c>create_entity</c> does
/// (<see cref="DenTools.CreateEntity"/>), and takes its den, universe, and
/// scope the way <c>wolfden copy</c> does: the note goes to the den's files
/// only, and nothing is committed.
/// </summary>
/// <remarks>
/// Exit codes: 0 when the note is made, and 2 when the command line is
/// wrong, there's no den, or the create is refused (an unknown type or
/// scope, a final book, a file name that's taken, or a den with several
/// universes and none named).
/// </remarks>
internal static class NewCommand
{
    internal static readonly CommandSpec Spec = new(
        "new",
        "usage: wolfden new <type> <name> [--in <scope>] [--from <position>] [--slug <slug>] [--den <den>] [--json]",
        """
        Creates a note from its type's template, such as a new character. It
        gets a new id that no file in the den uses, the name, and a file name
        made from it (with -2, -3 if it's taken), in the type's folder under
        entities/. Its status is the template's, or draft, and its sections
        are the template's, empty, for you to fill in. It's written to the
        den's files only; nothing is committed.

          <type>              the type's key, such as 'character', 'location', or
                              a type your den's own packs define
          <name>              the note's name (quote a name with spaces)
          --in <scope>        where it goes: '<universe>' (or '<universe>/') for
                              universe scope, or '<universe>/<key>' for a series
                              or book of it ('<universe>/series:<key>' or
                              '<universe>/book:<key>' if a series and a book share
                              the key); in a den with one universe, a bare series
                              or book key will do, unless it's the universe's key
                              too. By default, the den's universe, when it has
                              one. Never a final book.
          --from <position>   where the reader first meets it, such as
                              'dracula:5'; by default, the start of where it goes
          --slug <slug>       the file name without .md; by default, made from the
                              name
          --den <den>         the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --json              print one JSON document instead of text

        It prints the new file and where the note enters canon. Exits 0 when
        the note is made, and 2 if it isn't.
        """,
        ["--json"],
        ["--den", "--in", "--from", "--slug"],
        [],
        2,
        "give the type and the name, and nothing else (quote a name with spaces).");

    /// <summary>The command's options and commands, in place of the MCP tool's parameters and tools, in its messages.</summary>
    internal static readonly ToolWords Words = new(
        "", "as <name>", "--slug", "with --in", "use 'wolfden query --brief'", "run 'wolfden check'", "run 'wolfden check'", "run 'wolfden check'");

    /// <summary><c>--in</c>, where the note goes.</summary>
    private static readonly ScopeOption In = new("--in", "create it in", "created");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (line.Arguments.Count < 2)
        {
            error.WriteLine("wolfden new: give the note's type and its name, such as: wolfden new character \"Mina Harker\".");
            error.WriteLine(Spec.Usage);
            return CliApp.ExitUsage;
        }

        var name = line.Arguments[1];
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            error.WriteLine("wolfden new: give the note a name: one line of text.");
            return CliApp.ExitUsage;
        }

        if (DenLocation.Resolve(Spec.Name, line.Value("--den"), error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        string? universe = null, series = null, book = null;
        if (line.Value("--in") is { } scope && !In.TryParse(root, scope, out universe, out series, out book, out var problem))
        {
            error.WriteLine($"wolfden new: {problem}");
            return CliApp.ExitUsage;
        }

        WriteResult result;
        try
        {
            result = new DenTools(root) { Words = Words }.CreateEntity(
                line.Arguments[0], name, universe, series, book, line.Value("--slug"), from: line.Value("--from"));
        }
        catch (DenToolException e)
        {
            error.WriteLine($"wolfden new: {e.Message}");
            return CliApp.ExitUsage;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine($"wolfden new: couldn't read the den at '{root}' or its index: {e.Message}");
            return CliApp.ExitUsage;
        }

        if (line.Has("--json"))
        {
            CliJson.Write(output, result);
            return CliApp.ExitOk;
        }

        output.WriteLine($"Created {result.Path}");
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
}
