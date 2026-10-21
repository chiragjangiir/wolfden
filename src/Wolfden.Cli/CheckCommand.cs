using Wolfden.Core.Checks;
using Wolfden.Core.Documents;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden check [&lt;den&gt;] [--fix] [--json]</c>: runs every check on a
/// den and reports the errors and warnings (design.md §7, format.md §1.3).
/// </summary>
/// <remarks>
/// <para>
/// The default output is one line per diagnostic,
/// <c>path:line: severity rule: message</c> (the line is left out when there
/// isn't one), then a summary line. Paths are relative to the den root,
/// with <c>/</c>. <c>--json</c> prints a single JSON document instead
/// (<see cref="CheckReport"/>).
/// </para>
/// <para>
/// <c>--fix</c> first gives every container and entity file that loads
/// without an <c>id</c> a new one (F-ID-2, F-ID-6, <see cref="IdFix"/>),
/// reports each file it changed or had to skip, and then checks the den as
/// it is after the fix. Without <c>--fix</c>, check never writes.
/// </para>
/// <para>
/// Exit codes: 0 when the den has no errors (warnings alone don't fail it,
/// F-GEN-1), 1 when it has at least one error, and 2 when the command line
/// is wrong, the folder can't be read, <c>den.yaml</c> doesn't load, or
/// the check fails unexpectedly, so nothing else could be checked.
/// </para>
/// </remarks>
internal static class CheckCommand
{
    internal static readonly CommandSpec Spec = new(
        "check",
        "usage: wolfden check [<den>] [--den <den>] [--fix] [--json]",
        """
        Checks a den for errors and continuity problems: broken links, positions
        that don't resolve, missing ids, values that don't fit their fields,
        outlines, and the rest of the rules in format.md and types.md. It reads
        the files, not the index.

          <den>, --den <den>  the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --fix               first add a new id to every container and entity
                              file that has none (F-ID-2); nothing else changes.
                              Files in a final book and files with errors are
                              left alone.
          --json              print one JSON document instead of text

        Exits 0 if the den has no errors (warnings alone pass), 1 if it has at
        least one, and 2 if it couldn't be checked.
        """,
        ["--json", "--fix"],
        ["--den"],
        [],
        1,
        "give at most one den folder.");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) =>
        Run(args, output, error, DenCheck.Run, Environment.GetEnvironmentVariable("WOLFDEN_DEBUG") == "1");

    /// <summary>Runs the command with <paramref name="runCheck"/> in place of <see cref="DenCheck.Run(string)"/>, so tests can make it fail.</summary>
    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, Func<string, DenCheck> runCheck, bool debug = false)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (!DenLocation.TryGetGiven(Spec, line, error, out var given))
        {
            return CliApp.ExitUsage;
        }

        // A folder with no den.yaml is still checked, so the report says why it isn't a den (F-DEN-1).
        var path = given ?? DenLocation.FindAbove(Environment.CurrentDirectory) ?? ".";
        if (!Directory.Exists(path))
        {
            error.WriteLine($"wolfden check: there's no folder at '{path}'.");
            return CliApp.ExitUsage;
        }

        IReadOnlyList<IdFixResult> fixes = [];
        DenCheck check;
        try
        {
            if (line.Has("--fix"))
            {
                fixes = IdFix.Run(path);
            }

            check = runCheck(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"wolfden check: couldn't read the den at '{path}': {e.Message}");
            return CliApp.ExitUsage;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A bug, not a problem with the den: 2 still means "couldn't check".
            return Unexpected(error, debug, $"while checking '{path}'", e);
        }

        try
        {
            if (line.Has("--json"))
            {
                CliJson.Write(output, CheckReport.From(check, fixes));
            }
            else
            {
                WriteText(check, fixes, output);
            }
        }
        catch (IOException e)
        {
            // Such as a closed pipe: the report didn't get out, so the check didn't do its job.
            TryWrite(error, $"wolfden check: couldn't write the report: {e.Message}");
            return CliApp.ExitUsage;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Unexpected(error, debug, "while writing the report", e);
        }

        return !check.IsLoaded ? CliApp.ExitUsage
            : check.ErrorCount > 0 ? CliApp.ExitErrors
            : CliApp.ExitOk;
    }

    /// <summary>
    /// Reports a bug in one line, and with <c>WOLFDEN_DEBUG=1</c> the whole
    /// exception with its stack trace, so it can be reported.
    /// </summary>
    private static int Unexpected(TextWriter error, bool debug, string when, Exception e)
    {
        TryWrite(error, $"wolfden check: unexpected failure {when} ({e.GetType().Name}: {e.Message}). This is a bug in Wolfden; please report it."
            + (debug ? "" : " Set WOLFDEN_DEBUG=1 to see the details."));
        if (debug)
        {
            TryWrite(error, e.ToString());
        }

        return CliApp.ExitUsage;
    }

    /// <summary>Writes a line to standard error, ignoring a failure there, since there's nowhere left to report it.</summary>
    private static void TryWrite(TextWriter error, string message)
    {
        try
        {
            error.WriteLine(message);
        }
        catch (IOException)
        {
        }
    }

    private static void WriteText(DenCheck check, IReadOnlyList<IdFixResult> fixes, TextWriter output)
    {
        foreach (var fix in fixes)
        {
            output.WriteLine($"{fix.Path}: {FixMessage(fix)}");
        }

        if (fixes.Count > 0)
        {
            output.WriteLine();
        }

        foreach (var d in check.Diagnostics)
        {
            var line = d.Diagnostic.Line is int n ? $":{n}" : "";
            output.WriteLine($"{d.Path}{line}: {Severity(d.Diagnostic.Severity)} {d.Diagnostic.RuleId}: {d.Diagnostic.Message}");
        }

        if (check.Diagnostics.Count > 0)
        {
            output.WriteLine();
        }

        output.WriteLine(Summary(check));
    }

    internal static string FixMessage(IdFixResult fix) => fix.Outcome == IdFixOutcome.Added
        ? $"fixed F-ID-2: added id {fix.Id}"
        : $"not fixed F-ID-2: {fix.Reason}";

    internal static string Summary(DenCheck check)
    {
        if (!check.IsLoaded)
        {
            return $"{Count(check.ErrorCount, "error")}: den.yaml didn't load, so nothing else was checked.";
        }

        var files = check.Diagnostics.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count();
        var found = check.Diagnostics.Count == 0
            ? "No errors or warnings"
            : $"{Count(check.ErrorCount, "error")} and {Count(check.WarningCount, "warning")} in {Count(files, "file")}";
        var spoilers = check.Structure.SpoilerChecks ? "on" : "off";
        return $"{found}. Checked {Count(check.Catalog.Entities.Count, "entity", "entities")}; spoiler checks are {spoilers}.";
    }

    internal static string Severity(DiagnosticSeverity severity) => severity == DiagnosticSeverity.Error ? "error" : "warning";

    internal static string Count(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}

/// <summary>
/// The JSON shape of <c>wolfden check --json</c>. <see cref="Version"/> is
/// the shape's version: fields may be added without changing it, but a
/// field is never renamed, removed, or given a new meaning without a new
/// version.
/// </summary>
/// <param name="Version">The report shape's version, 1.</param>
/// <param name="Den">What was checked.</param>
/// <param name="Summary">The counts.</param>
/// <param name="Diagnostics">Every diagnostic, sorted as <see cref="DenCheck.Diagnostics"/> is.</param>
/// <param name="Fixes">What <c>--fix</c> did, file by file, in path order; empty without <c>--fix</c>.</param>
internal sealed record CheckReport(
    int Version, CheckReport.DenInfo Den, CheckReport.Counts Summary, IReadOnlyList<CheckReport.Item> Diagnostics, IReadOnlyList<CheckReport.Fix> Fixes)
{
    /// <summary>What was checked.</summary>
    /// <param name="Loaded">True if <c>den.yaml</c> loaded. If false, only den.yaml was checked.</param>
    /// <param name="Format">The den's format number, or null if <c>format</c> didn't parse. It's set even when another key keeps the den from loading.</param>
    /// <param name="SpoilerChecks">True if the den turns on spoiler checks (F-DEN-7), or null if den.yaml didn't load.</param>
    /// <param name="Entities">The number of entities that loaded and were checked.</param>
    internal sealed record DenInfo(bool Loaded, int? Format, bool? SpoilerChecks, int Entities);

    /// <summary>The counts.</summary>
    /// <param name="Errors">Errors.</param>
    /// <param name="Warnings">Warnings.</param>
    /// <param name="Files">Files with at least one diagnostic.</param>
    internal sealed record Counts(int Errors, int Warnings, int Files);

    /// <summary>One diagnostic.</summary>
    /// <param name="Path">The file, relative to the den root, with <c>/</c>.</param>
    /// <param name="Line">The 1-based line, or null when the problem has no line.</param>
    /// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
    /// <param name="Rule">The rule id from format.md or types.md, such as <c>F-LINK-8</c>.</param>
    /// <param name="Message">The message for the writer.</param>
    internal sealed record Item(string Path, int? Line, string Severity, string Rule, string Message);

    /// <summary>One file <c>--fix</c> changed or had to leave alone.</summary>
    /// <param name="Path">The file, relative to the den root, with <c>/</c>.</param>
    /// <param name="Rule">The rule it fixes, <c>F-ID-2</c>.</param>
    /// <param name="Outcome"><c>fixed</c> or <c>skipped</c>.</param>
    /// <param name="Id">The id it was given, or null if it was skipped.</param>
    /// <param name="Message">What happened, for the writer.</param>
    internal sealed record Fix(string Path, string Rule, string Outcome, string? Id, string Message);

    public static CheckReport From(DenCheck check, IReadOnlyList<IdFixResult>? fixes = null) => new(
        1,
        new DenInfo(check.IsLoaded, check.Structure.Format, check.IsLoaded ? check.Structure.SpoilerChecks : null, check.Catalog.Entities.Count),
        new Counts(check.ErrorCount, check.WarningCount, check.Diagnostics.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count()),
        check.Diagnostics
            .Select(d => new Item(d.Path, d.Diagnostic.Line, CheckCommand.Severity(d.Diagnostic.Severity), d.Diagnostic.RuleId, d.Diagnostic.Message))
            .ToList(),
        (fixes ?? [])
            .Select(f => new Fix(f.Path, "F-ID-2", f.Outcome == IdFixOutcome.Added ? "fixed" : "skipped", f.Id, CheckCommand.FixMessage(f)))
            .ToList());
}
