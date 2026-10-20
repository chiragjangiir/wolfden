using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wolfden.Index;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden index [&lt;den&gt;] [--watch] [--json]</c>: rebuilds the den's
/// index, <c>.wolfden/index.db</c>, from its files (design.md §7), and with
/// <c>--watch</c> keeps it up to date as the files change (#14). The index
/// is derived data, so a rebuild loses nothing. It never writes to the den's
/// own files.
/// </summary>
/// <remarks>
/// Exit codes: 0 when the index is rebuilt, even if some files have errors
/// (they're left out of it, F-GEN-2, and <c>wolfden check</c> lists them),
/// or when watching stops with Ctrl+C; and 2 when the command line is wrong,
/// there's no den, <c>den.yaml</c> doesn't load (without <c>--watch</c>),
/// the index couldn't be written, or another Wolfden is already watching
/// the den.
/// </remarks>
internal static class IndexCommand
{
    internal static readonly CommandSpec Spec = new(
        "index",
        "usage: wolfden index [<den>] [--den <den>] [--watch] [--json]",
        """
        Rebuilds the den's index, .wolfden/index.db, from its files. The index
        holds nothing that isn't in the files, so a rebuild loses nothing, and
        'wolfden query' rebuilds it by itself when the files have changed.

          <den>, --den <den>  the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --watch             keep the index up to date as files change, in an
                              editor or by an AI agent, until Ctrl+C: it reads
                              only the changed notes, rebuilds when den.yaml, a
                              pack, or a universe, series, or book file changes,
                              and prints one line for each change it applies
          --json              print one JSON document instead of text; with
                              --watch, one JSON object per line

        Exits 0 when the index is rebuilt, even if some files have errors (run
        'wolfden check' to see them), or when watching stops; and 2 if it
        couldn't be rebuilt, or another Wolfden is already watching the den.
        """,
        ["--json", "--watch"],
        ["--den"],
        [],
        1,
        "give at most one den folder.");

    /// <summary>Compact JSON, one object per line, for <c>--watch --json</c>.</summary>
    private static readonly JsonSerializerOptions JsonLines = new(CliJson.Options) { WriteIndented = false };

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken stop = default)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (!DenLocation.TryGetGiven(Spec, line, error, out var given) || DenLocation.Resolve(Spec.Name, given, error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        if (line.Has("--watch"))
        {
            return Watch(root, line.Has("--json"), output, error, stop);
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

    /// <summary>
    /// <c>--watch</c>: rebuilds the index, then keeps it up to date until
    /// <paramref name="stop"/> is cancelled (Ctrl+C), printing one line per
    /// batch of changes. Failures are reported and watching goes on.
    /// </summary>
    private static int Watch(string root, bool json, TextWriter output, TextWriter error, CancellationToken stop)
    {
        var gate = new object();
        using var watcher = new DenWatcher(root);
        watcher.Updated += (_, update) =>
        {
            lock (gate)
            {
                Report(update, json, output);
            }
        };
        watcher.Failed += (_, e) =>
        {
            lock (gate)
            {
                error.WriteLine($"wolfden index: {e.Message}");
            }
        };

        IndexUpdate first;
        try
        {
            first = watcher.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            error.WriteLine($"wolfden index: couldn't watch the den at '{root}': {e.Message}");
            return CliApp.ExitUsage;
        }

        lock (gate)
        {
            Report(first, json, output);
            if (!json)
            {
                output.WriteLine("Watching for changes. Press Ctrl+C to stop.");
            }

            output.Flush();
        }

        CliApp.RunsUntilStopped = true;
        try
        {
            stop.WaitHandle.WaitOne();
        }
        finally
        {
            CliApp.RunsUntilStopped = false;
        }

        watcher.Dispose();
        lock (gate)
        {
            if (!json)
            {
                output.WriteLine("Stopped watching.");
            }

            output.Flush();
        }

        return CliApp.ExitOk;
    }

    /// <summary>One line for a batch: what changed, how long it took, and what the index holds now.</summary>
    private static void Report(IndexUpdate update, bool json, TextWriter output)
    {
        var s = update.Summary;
        if (json)
        {
            output.Write(JsonSerializer.Serialize(
                new WatchEvent(
                    1, update.Kind == IndexUpdateKind.Rebuilt ? "rebuilt" : "updated", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    update.Reason, update.Files, update.Rows, (long)update.Elapsed.TotalMilliseconds,
                    s.DenLoaded, s.Files, s.Universes, s.Entities, s.Links, s.Errors, s.Warnings),
                JsonLines) + "\n");
        }
        else
        {
            var what = update.Kind == IndexUpdateKind.Rebuilt
                ? $"Rebuilt {IndexBuilder.StateFolder}/{IndexBuilder.IndexFileName} ({update.Reason})"
                : update.Files.Count == 1
                    ? $"Updated {update.Files[0]}"
                    : $"Updated {CheckCommand.Count(update.Files.Count, "file")}";
            var holds = s.DenLoaded
                ? $"{CheckCommand.Count(s.Universes, "universe")}, {CheckCommand.Count(s.Entities, "entity", "entities")}, " +
                  $"{CheckCommand.Count(s.Links, "link")}; {CheckCommand.Count(s.Errors, "error")}, {CheckCommand.Count(s.Warnings, "warning")}"
                : "den.yaml doesn't load, so nothing is indexed; run 'wolfden check' to see why";
            output.WriteLine(
                $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {what} in {Milliseconds(update.Elapsed)}: {holds}.");
        }

        output.Flush();
    }

    private static string Milliseconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 10
            ? $"{elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s"
            : elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s"
                : $"{Math.Max(1, (int)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)} ms";

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

    /// <summary>The JSON shape of each line of <c>wolfden index --watch --json</c>.</summary>
    /// <param name="Version">The shape's version, 1.</param>
    /// <param name="Event"><c>rebuilt</c> (the first build, or a change a rebuild handles) or <c>updated</c> (only the changed files read).</param>
    /// <param name="Time">When it finished, in UTC (ISO 8601).</param>
    /// <param name="Reason">For <c>rebuilt</c>, why; otherwise null.</param>
    /// <param name="ChangedFiles">For <c>updated</c>, the changed files the index reads, relative to the den root; otherwise empty.</param>
    /// <param name="Rows">The index rows written.</param>
    /// <param name="Milliseconds">How long it took.</param>
    /// <param name="DenLoaded">False if <c>den.yaml</c> doesn't load, so nothing is indexed.</param>
    /// <param name="Files">The files the index was built from.</param>
    /// <param name="Universes">The universes indexed.</param>
    /// <param name="Entities">The entities indexed.</param>
    /// <param name="Links">The links indexed.</param>
    /// <param name="Errors">Errors found while loading.</param>
    /// <param name="Warnings">Warnings found while loading.</param>
    internal sealed record WatchEvent(
        int Version, string Event, string Time, string? Reason, IReadOnlyList<string> ChangedFiles, int Rows, long Milliseconds,
        bool DenLoaded, int Files, int Universes, int Entities, int Links, int Errors, int Warnings);
}
