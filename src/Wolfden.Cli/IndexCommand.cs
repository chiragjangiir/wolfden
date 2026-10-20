using Microsoft.Data.Sqlite;
using Wolfden.Index;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden index [&lt;den&gt;] [--json]</c>: rebuilds the den's index,
/// <c>.wolfden/index.db</c>, from its files (design.md §7). The index is
/// derived data, so a rebuild loses nothing. It never writes to the den's
/// own files.
/// </summary>
/// <remarks>
/// Exit codes: 0 when the index is rebuilt, even if some files have errors
/// (they're left out of it, F-GEN-2, and <c>wolfden check</c> lists them),
/// and 2 when the command line is wrong, there's no den, <c>den.yaml</c>
/// doesn't load, or the index couldn't be written.
/// </remarks>
internal static class IndexCommand
{
    internal static readonly CommandSpec Spec = new(
        "index",
        "usage: wolfden index [<den>] [--den <den>] [--json]",
        """
        Rebuilds the den's index, .wolfden/index.db, from its files. The index
        holds nothing that isn't in the files, so a rebuild loses nothing, and
        'wolfden query' rebuilds it by itself when the files have changed.

          <den>, --den <den>  the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --json              print one JSON document instead of text

        Exits 0 when the index is rebuilt, even if some files have errors (run
        'wolfden check' to see them), and 2 if it couldn't be.
        """,
        ["--json"],
        ["--den"],
        [],
        1,
        "give at most one den folder.");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (!DenLocation.TryGetGiven(Spec, line, error, out var given) || DenLocation.Resolve(Spec.Name, given, error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        IndexReport report;
        bool loaded;
        try
        {
            IndexBuilder.Rebuild(root);
            using var index = DenIndex.Open(root);
            var db = index.Context;
            loaded = db.Dens.Single().IsLoaded;
            report = new IndexReport(
                1,
                $"{IndexBuilder.StateFolder}/{IndexBuilder.IndexFileName}",
                db.Files.Count(),
                db.Containers.Count(c => c.Kind == "universe"),
                db.Entities.Count(),
                db.Links.Count(),
                db.Diagnostics.Count(d => d.Severity == "error"),
                db.Diagnostics.Count(d => d.Severity == "warning"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine($"wolfden index: couldn't rebuild the index of the den at '{root}': {e.Message}");
            return CliApp.ExitUsage;
        }

        if (!loaded)
        {
            error.WriteLine($"wolfden index: den.yaml in '{root}' doesn't load, so nothing could be indexed. Run 'wolfden check' to see why.");
            return CliApp.ExitUsage;
        }

        if (line.Has("--json"))
        {
            CliJson.Write(output, report);
        }
        else
        {
            output.WriteLine(
                $"Rebuilt {report.Index}: {CheckCommand.Count(report.Universes, "universe")}, " +
                $"{CheckCommand.Count(report.Entities, "entity", "entities")}, and {CheckCommand.Count(report.Links, "link")} " +
                $"from {CheckCommand.Count(report.Files, "file")}.");
            if (report.Errors + report.Warnings > 0)
            {
                output.WriteLine(
                    $"Loading found {CheckCommand.Count(report.Errors, "error")} and {CheckCommand.Count(report.Warnings, "warning")}; " +
                    "files with errors are left out. Run 'wolfden check' for details.");
            }
        }

        return CliApp.ExitOk;
    }

    /// <summary>The JSON shape of <c>wolfden index --json</c>.</summary>
    /// <param name="Version">The shape's version, 1.</param>
    /// <param name="Index">The index file, relative to the den root, with <c>/</c>.</param>
    /// <param name="Files">The files read.</param>
    /// <param name="Universes">The universes indexed.</param>
    /// <param name="Entities">The entities indexed.</param>
    /// <param name="Links">The links indexed.</param>
    /// <param name="Errors">Errors found while loading. <c>wolfden check</c> lists them, with its continuity checks too.</param>
    /// <param name="Warnings">Warnings found while loading.</param>
    internal sealed record IndexReport(int Version, string Index, int Files, int Universes, int Entities, int Links, int Errors, int Warnings);
}
