using System.Globalization;
using Wolfden.Core.Structure;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden new-id [&lt;count&gt;] [--json]</c>: prints new ids, one per
/// line (format.md F-ID-5), for a writer making files by hand. It doesn't
/// need a den and never writes.
/// </summary>
internal static class NewIdCommand
{
    internal const int MaxCount = 1000;

    internal static readonly CommandSpec Spec = new(
        "new-id",
        "usage: wolfden new-id [<count>] [--json]",
        $"""
        Prints new ids for files you make by hand (format.md §4.2), one per line.
        Paste one into a new file's frontmatter as 'id: <id>'.

          <count>  how many, from 1 to {MaxCount}; by default, 1
          --json   print one JSON document instead of text

        Exits 0, or 2 if the command line is wrong.
        """,
        ["--json"],
        [],
        [],
        1,
        "give at most one count.");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        var count = 1;
        if (line.Arguments.Count > 0
            && (!int.TryParse(line.Arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count is < 1 or > MaxCount))
        {
            error.WriteLine($"wolfden new-id: the count must be a whole number from 1 to {MaxCount}, not '{line.Arguments[0]}'.");
            error.WriteLine(Spec.Usage);
            return CliApp.ExitUsage;
        }

        var ids = Enumerable.Range(0, count).Select(_ => Ids.New()).ToList();
        if (line.Has("--json"))
        {
            CliJson.Write(output, new NewIdReport(1, ids));
        }
        else
        {
            foreach (var id in ids)
            {
                output.WriteLine(id);
            }
        }

        return CliApp.ExitOk;
    }

    /// <summary>The JSON shape of <c>wolfden new-id --json</c>.</summary>
    /// <param name="Version">The shape's version, 1.</param>
    /// <param name="Ids">The new ids.</param>
    internal sealed record NewIdReport(int Version, IReadOnlyList<string> Ids);
}
