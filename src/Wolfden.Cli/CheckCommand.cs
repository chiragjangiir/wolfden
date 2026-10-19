using System.Text.Encodings.Web;
using System.Text.Json;
using Wolfden.Core.Checks;
using Wolfden.Core.Documents;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden check [&lt;den&gt;] [--json]</c>: runs every check on a den and
/// reports the errors and warnings (design.md §7, format.md §1.3).
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
/// Exit codes: 0 when the den has no errors (warnings alone don't fail it,
/// F-GEN-1), 1 when it has at least one error, and 2 when the command line
/// is wrong, the folder can't be read, or <c>den.yaml</c> doesn't load, so
/// nothing else could be checked.
/// </para>
/// </remarks>
internal static class CheckCommand
{
    private const string Usage = "usage: wolfden check [<den>] [--json]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep names and quotes readable ("Zoë", 'x'). The JSON never goes into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n",
    };

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var json = false;
        string? path = null;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return CliApp.ExitOk;
                case var option when option.StartsWith('-') && option.Length > 1:
                    error.WriteLine($"wolfden check: unknown option '{option}'.");
                    error.WriteLine(Usage);
                    return CliApp.ExitUsage;
                case var _ when path is not null:
                    error.WriteLine("wolfden check: give at most one den folder.");
                    error.WriteLine(Usage);
                    return CliApp.ExitUsage;
                default:
                    path = arg;
                    break;
            }
        }

        path ??= ".";
        if (!Directory.Exists(path))
        {
            error.WriteLine($"wolfden check: there's no folder at '{path}'.");
            return CliApp.ExitUsage;
        }

        DenCheck check;
        try
        {
            check = DenCheck.Run(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"wolfden check: couldn't read the den at '{path}': {e.Message}");
            return CliApp.ExitUsage;
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(CheckReport.From(check), JsonOptions));
        }
        else
        {
            WriteText(check, output);
        }

        return !check.IsLoaded ? CliApp.ExitUsage
            : check.ErrorCount > 0 ? CliApp.ExitErrors
            : CliApp.ExitOk;
    }

    private static void WriteText(DenCheck check, TextWriter output)
    {
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

    private static string Count(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
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
internal sealed record CheckReport(int Version, CheckReport.DenInfo Den, CheckReport.Counts Summary, IReadOnlyList<CheckReport.Item> Diagnostics)
{
    /// <summary>What was checked.</summary>
    /// <param name="Loaded">True if <c>den.yaml</c> loaded. If false, only den.yaml was checked.</param>
    /// <param name="Format">The den's format number, or null if it didn't load.</param>
    /// <param name="SpoilerChecks">True if the den turns on spoiler checks (F-DEN-7).</param>
    /// <param name="Entities">The number of entities that loaded and were checked.</param>
    internal sealed record DenInfo(bool Loaded, int? Format, bool SpoilerChecks, int Entities);

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

    public static CheckReport From(DenCheck check) => new(
        1,
        new DenInfo(check.IsLoaded, check.Structure.Format, check.Structure.SpoilerChecks, check.Catalog.Entities.Count),
        new Counts(check.ErrorCount, check.WarningCount, check.Diagnostics.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count()),
        check.Diagnostics
            .Select(d => new Item(d.Path, d.Diagnostic.Line, CheckCommand.Severity(d.Diagnostic.Severity), d.Diagnostic.RuleId, d.Diagnostic.Message))
            .ToList());
}
