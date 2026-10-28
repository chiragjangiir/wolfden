using Microsoft.Data.Sqlite;
using Wolfden.Git;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden freeze &lt;book&gt;</c> and <c>wolfden unfreeze &lt;book&gt;</c>
/// (#31; design.md §5): mark a finished book final, so its notes, chapters,
/// and outline can't be edited, or open it again. With git history, a
/// freeze checkpoints the den and tags the snapshot, and an unfreeze
/// checkpoints (<see cref="DenTools.FreezeBook"/>,
/// <see cref="DenTools.UnfreezeBook"/>). They're the writer's: AI agents
/// get no MCP tool for them.
/// </summary>
/// <remarks>
/// Exit codes: 0 when the book is frozen (or unfrozen) and its snapshot is
/// taken, or there's no history to take one in, and when there's nothing
/// to do; 2 when the command line is wrong, there's no den or no such
/// book, or a step in the den's history failed. Then the book's status has
/// still changed, and the message says what to do: for a freeze, run it
/// again once the problem is fixed.
/// </remarks>
internal static class FreezeCommand
{
    internal static readonly CommandSpec FreezeSpec = new(
        "freeze",
        "usage: wolfden freeze <book> [--universe <key>] [--den <den>] [--json]",
        """
        Freezes a finished book: marks it 'status: final' in its book.md, so its
        notes, its chapters, and its outline can't be edited until it's
        unfrozen. When the den has git history, it then makes a checkpoint of
        the whole den and tags it 'book/<universe>/<book>/final' (or final-2,
        final-3, and so on, for a book frozen before), a snapshot to come back
        to. Without history, the status alone is the freeze.

          <book>              the book's key, such as 'dracula'
          --universe <key>    the book's universe, when the den has several
          --den <den>         the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --json              print one JSON document instead of text

        If git refuses the checkpoint or the tag, the book stays final and the
        command says why. Run it again once that's fixed: it takes the
        checkpoint and adds the tag that are missing. Freezing a book that's
        frozen and tagged does nothing. Exits 0 when the book is frozen, and 2
        if it isn't, or a step in the history failed.
        """,
        ["--json"],
        ["--den", "--universe"],
        [],
        1,
        "give one book's key.");

    internal static readonly CommandSpec UnfreezeSpec = new(
        "unfreeze",
        "usage: wolfden unfreeze <book> [--universe <key>] [--den <den>] [--json]",
        """
        Unfreezes a book, so it can be edited again: writes 'status: draft' in
        its book.md. When the den has git history, it then makes a checkpoint,
        so the history shows when the book was reopened. The book's freeze tags
        stay where they are.

          <book>              the book's key, such as 'dracula'
          --universe <key>    the book's universe, when the den has several
          --den <den>         the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --json              print one JSON document instead of text

        If git refuses the checkpoint, the book is still unfrozen, and the
        command says why. Exits 0 when the book is unfrozen (or wasn't frozen),
        and 2 if it isn't, or the checkpoint failed.
        """,
        ["--json"],
        ["--den", "--universe"],
        [],
        1,
        "give one book's key.");

    /// <summary>The command's options and commands, in place of the MCP tools' parameters and tools, in its messages.</summary>
    internal static readonly ToolWords Words = new(
        "", "", "", "with --universe", "use 'wolfden query --brief'", "run 'wolfden check'", "run 'wolfden check'", "run 'wolfden check'");

    /// <summary>Snapshots that mean a step in the den's history failed, so the command exits 2.</summary>
    private static readonly string[] Failures = [FreezeSnapshot.CheckpointFailed, FreezeSnapshot.TagFailed];

    public static int Run(bool freeze, IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var spec = freeze ? FreezeSpec : UnfreezeSpec;
        if (CommandLine.Parse(spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (line.Arguments.Count < 1)
        {
            error.WriteLine($"wolfden {spec.Name}: give the book's key.");
            error.WriteLine(spec.Usage);
            return CliApp.ExitUsage;
        }

        if (DenLocation.Resolve(spec.Name, line.Value("--den"), error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        FreezeResult result;
        try
        {
            var tools = new DenTools(root) { Words = Words, History = () => GitHistory.ForDen(root, CliApp.Git) };
            result = freeze
                ? tools.FreezeBook(line.Arguments[0], null, line.Value("--universe"))
                : tools.UnfreezeBook(line.Arguments[0], null, line.Value("--universe"));
        }
        catch (DenToolException e)
        {
            error.WriteLine($"wolfden {spec.Name}: {e.Message}");
            return CliApp.ExitUsage;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine($"wolfden {spec.Name}: couldn't read the den at '{root}' or its index: {e.Message}");
            return CliApp.ExitUsage;
        }

        var failed = Failures.Contains(result.Snapshot, StringComparer.Ordinal);
        if (line.Has("--json"))
        {
            CliJson.Write(output, result);
        }
        else if (failed)
        {
            error.WriteLine($"wolfden {spec.Name}: {result.Message}");
        }
        else
        {
            output.WriteLine(result.Message);
        }

        if (!line.Has("--json"))
        {
            foreach (var w in result.Warnings)
            {
                output.WriteLine($"{w.Path}:{w.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}: {w.Severity} {w.Rule}: {w.Message}");
            }
        }

        return failed ? CliApp.ExitUsage : CliApp.ExitOk;
    }
}
