using System.Text.Json;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden check</c>: the text and JSON output, the exit codes, and a
/// run over <c>samples/dracula</c>. The rules themselves are tested in
/// Core (<c>ContinuityCheckTests</c>).
/// </summary>
public class CheckCommandTests
{
    private const string A1 = "universes/vael/series/a/books/a1";
    private const string A2 = "universes/vael/series/a/books/a2";

    /// <summary>
    /// An invented den with one error and a warning of each kind the
    /// output has to show: one with a line, one without (F-KEY-4), a spoiler
    /// (F-LINK-13), and non-ASCII text in a message.
    /// </summary>
    private static readonly Dictionary<string, string> MixedDen = new()
    {
        ["den.yaml"] = "format: 1\nspoiler_checks: true\n",
        ["universes/vael/universe.md"] = "---\nid: 01JC5CK1000000000000000001\nname: Vael\norder: [a]\n---\n",
        ["universes/vael/series/a/series.md"] = "---\nid: 01JC5CK1000000000000000002\nname: A\nbooks: [a1, a2]\n---\n",
        [$"{A1}/book.md"] = "---\nid: 01JC5CK1000000000000000003\nname: Book 1\nchapters: [1, 2, 3]\n---\n",
        [$"{A2}/book.md"] = "---\nid: 01JC5CK1000000000000000004\nname: Book 2\nchapters: [1, 2]\n---\n",
        [$"{A1}/entities/characters/orrin-fell.md"] = """
            ---
            id: 01JC5CK1000000000000000005
            type: character
            name: Orrin Fell
            status: canon
            fields:
              role:
                - { value: deckhand }
                - { value: captain, from: "a1:9" }
            ---

            ## Overview
            Sails with [[Zoë Marr]]. Owes [[the-stranger]] a debt.
            """,
        [$"{A1}/entities/characters/torn.md"] = "---\nid: 01JC5CK1000000000000000006\ntype: character\nname: [Torn\n---\n",
        [$"{A1}/entities/notes/Old Notes.md"] = "---\nid: 01JC5CK1000000000000000007\ntype: note\nname: Old notes\nstatus: draft\n---\n",
        [$"{A2}/entities/characters/the-stranger.md"] = "---\nid: 01JC5CK1000000000000000008\ntype: character\nname: The Stranger\nstatus: canon\n---\n",
    };

    private static (int ExitCode, string Output, string Error) Check(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliApp.Run(["check", .. args], output, error);
        return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TextOutputHasOneLinePerDiagnosticThenASummary()
    {
        using var den = TempDen.FromFiles(MixedDen);

        var (exitCode, output, error) = Check(den.Root);

        Assert.Equal(CliApp.ExitErrors, exitCode);
        Assert.Empty(error);
        Assert.Equal(
            $"""
            {A1}/entities/characters/orrin-fell.md:9: warning F-POS-4: a1:9: Book 'a1' has no chapter '9'. Until it resolves, the fact that uses it is left out of canon views.
            {A1}/entities/characters/orrin-fell.md:13: warning F-LINK-13: [[the-stranger]]: 'The Stranger' enters canon at the start of book 'a2', after this link's position, the start of book 'a1'. It may be a spoiler.
            {A1}/entities/characters/orrin-fell.md:13: warning F-LINK-8: [[Zoë Marr]]: 'Zoë Marr' matches no entity in universe 'vael'.
            {A1}/entities/characters/torn.md:5: error F-YAML-1: {ErrorMessage(den)}
            {A1}/entities/notes/Old Notes.md: warning F-KEY-4: The file name 'Old Notes.md' isn't a valid slug (lowercase letters and digits, with single hyphens between words), so links reach this entity only by id, name, or alias.

            1 error and 4 warnings in 3 files. Checked 3 entities; spoiler checks are on.

            """,
            output);
    }

    /// <summary>The YAML parser's message for torn.md, which the test doesn't pin down.</summary>
    private static string ErrorMessage(TempDen den) =>
        Core.Checks.DenCheck.Run(den.Root).Diagnostics.Single(d => d.Diagnostic.RuleId == "F-YAML-1").Diagnostic.Message;

    [Fact]
    [Trait("Scenario", "SC-CHECK-12")]
    public void JsonOutputMatchesTheGoldenFile()
    {
        using var den = TempDen.FromFiles(MixedDen);

        // Raw output, not line-ending-normalized: the JSON is LF on every OS,
        // and the golden file is checked out with LF (.gitattributes).
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = CliApp.Run(["check", den.Root, "--json"], output, error);

        Assert.Equal(CliApp.ExitErrors, exitCode);
        Assert.Empty(error.ToString());
        var golden = Path.Combine(TempDen.RepoRoot, "tests", "Wolfden.Cli.Tests", "Golden", "check-mixed.json");
        if (Environment.GetEnvironmentVariable("WOLFDEN_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(golden, output.ToString());
        }

        Assert.DoesNotContain("\r", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(golden), output.ToString());
    }

    [Fact]
    public void AnUnexpectedFailureExitsTwoWithAShortMessage()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CheckCommand.Run([TempDen.DraculaRoot], output, error, _ => throw new InvalidOperationException("boom"));

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith("wolfden check: unexpected failure while checking ", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("(InvalidOperationException: boom)", error.ToString(), StringComparison.Ordinal);
        Assert.Single(error.ToString().TrimEnd().Split('\n'));
        Assert.Contains("Set WOLFDEN_DEBUG=1 to see the details.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithDebugOnAnUnexpectedFailurePrintsTheStackTrace()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CheckCommand.Run([TempDen.DraculaRoot], output, error, _ => throw new InvalidOperationException("boom"), debug: true);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Contains("(InvalidOperationException: boom)", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(" at ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReportThatCantBeWrittenExitsTwo()
    {
        // Such as output piped to a program that has already exited.
        var error = new StringWriter();

        var exitCode = CheckCommand.Run([TempDen.DraculaRoot], new FailingWriter(), error, Core.Checks.DenCheck.Run);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal("wolfden check: couldn't write the report: The pipe has been closed.\n", error.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>A writer whose every write fails, as a closed pipe's does.</summary>
    private sealed class FailingWriter : StringWriter
    {
        public override void Write(char value) => throw new IOException("The pipe has been closed.");

        public override void Write(string? value) => throw new IOException("The pipe has been closed.");

        public override void Write(char[] buffer, int index, int count) => throw new IOException("The pipe has been closed.");

        public override void WriteLine(string? value) => throw new IOException("The pipe has been closed.");
    }

    [Fact]
    public void JsonOutputHasAStableShape()
    {
        using var den = TempDen.FromFiles(MixedDen);

        var (_, output, _) = Check("--json", den.Root);

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(["version", "den", "summary", "diagnostics", "fixes"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(["loaded", "format", "spoilerChecks", "entities"], root.GetProperty("den").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["errors", "warnings", "files"], root.GetProperty("summary").EnumerateObject().Select(p => p.Name));
        foreach (var item in root.GetProperty("diagnostics").EnumerateArray())
        {
            Assert.Equal(["path", "line", "severity", "rule", "message"], item.EnumerateObject().Select(p => p.Name));
            Assert.Contains(item.GetProperty("severity").GetString(), new[] { "error", "warning" });
            Assert.True(item.GetProperty("line").ValueKind is JsonValueKind.Number or JsonValueKind.Null);
        }

        // A diagnostic with no line has "line": null, not a missing key.
        Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), i => i.GetProperty("line").ValueKind == JsonValueKind.Null);
    }

    // ----- Exit codes -----

    [Fact]
    public void ACleanDenExitsZero()
    {
        var (exitCode, output, error) = Check(TempDen.DraculaRoot);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.Equal("No errors or warnings. Checked 50 entities; spoiler checks are on.\n", output);
    }

    [Fact]
    public void TheDraculaSampleIsCleanInJsonToo()
    {
        var (exitCode, output, _) = Check(TempDen.DraculaRoot, "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(0, json.RootElement.GetProperty("summary").GetProperty("errors").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("summary").GetProperty("warnings").GetInt32());
        Assert.Equal(50, json.RootElement.GetProperty("den").GetProperty("entities").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-1")]
    public void WarningsAloneExitZero()
    {
        // F-GEN-1: a renamed character's broken links are warnings, so check passes.
        var files = new Dictionary<string, string>(MixedDen);
        files.Remove($"{A1}/entities/characters/torn.md");
        using var den = TempDen.FromFiles(files);

        var (exitCode, output, _) = Check(den.Root);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.EndsWith("0 errors and 4 warnings in 2 files. Checked 3 entities; spoiler checks are on.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CHECK-9")]
    public void AnErrorExitsOne()
    {
        using var den = TempDen.FromFiles(MixedDen);

        Assert.Equal(CliApp.ExitErrors, Check(den.Root).ExitCode);
    }

    [Fact]
    public void AFolderWithoutDenYamlExitsTwo()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["notes.md"] = "# Notes\n" });

        var (exitCode, output, _) = Check(den.Root);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Equal(
            "den.yaml: error F-DEN-1: This folder has no den.yaml, so it isn't a den. Add one with 'format: 1'.\n\n" +
            "1 error: den.yaml didn't load, so nothing else was checked.\n",
            output);
    }

    [Fact]
    public void ADenYamlThatDoesntLoadExitsTwoAndStillReportsWhyInJson()
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: 2\n" });

        var (exitCode, output, _) = Check(den.Root, "--json");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("den").GetProperty("loaded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("den").GetProperty("format").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("den").GetProperty("spoilerChecks").ValueKind);
        Assert.Equal("F-DEN-1", json.RootElement.GetProperty("diagnostics")[0].GetProperty("rule").GetString());
    }

    [Theory]
    [InlineData("authors: [wren-calder]\npacks: [nope]\n")]
    [InlineData("packs: nope\n")]
    public void ADenYamlThatDoesntLoadStillReportsTheFormatItParsedInJson(string rest)
    {
        using var den = TempDen.FromFiles(new Dictionary<string, string> { ["den.yaml"] = "format: 1\n" + rest });

        var (exitCode, output, _) = Check(den.Root, "--json");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("den").GetProperty("loaded").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("den").GetProperty("format").GetInt32());
    }

    [Fact]
    public void AMissingFolderExitsTwo()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wolfden-cli-tests", "no-such-den-" + Guid.NewGuid().ToString("N"));

        var (exitCode, output, error) = Check(missing);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith("wolfden check: there's no folder at ", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--frob")]
    [InlineData("-q")]
    public void AnUnknownOptionExitsTwo(string option)
    {
        var (exitCode, output, error) = Check(TempDen.DraculaRoot, option);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith($"wolfden check: unknown option '{option}'.\nusage: wolfden check", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFoldersExitTwo()
    {
        var (exitCode, _, error) = Check(TempDen.DraculaRoot, TempDen.DraculaRoot);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden check: give at most one den folder.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpPrintsUsageAndExitsZero()
    {
        var (exitCode, output, _) = Check("--help");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("usage: wolfden check [<den>] [--den <den>] [--fix] [--json]\n\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDenCanBeNamedWithTheDenOption()
    {
        var (exitCode, output, _) = Check("--den", TempDen.DraculaRoot);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("No errors or warnings. Checked 50 entities", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADenGivenBothWaysExitsTwo()
    {
        var (exitCode, output, error) = Check(TempDen.DraculaRoot, "--den", TempDen.DraculaRoot);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith("wolfden check: give the den folder once, as an argument or with '--den', not both.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckNeverWritesToTheDen()
    {
        using var den = TempDen.FromFiles(MixedDen);
        var before = Cli.Snapshot(den.Root);

        Check(den.Root);
        Check(den.Root, "--json");

        Assert.Equal(before, Cli.Snapshot(den.Root));
    }

    // ----- --fix -----

    /// <summary>A den whose universe, book, and one entity have no id, with a frozen book and a file with an error.</summary>
    private static readonly Dictionary<string, string> NoIdsDen = new()
    {
        ["den.yaml"] = "format: 1\n",
        ["universes/vael/universe.md"] = "---\nname: Vael\norder: [a]\n---\n\nThe world.\n",
        ["universes/vael/series/a/series.md"] = "---\nid: 01JC5CK2000000000000000002\nname: A\nbooks: [a1, a2]\n---\n",
        ["universes/vael/series/a/books/a1/book.md"] = "---\nname: Book 1\nchapters: [1, 2]\n---\n",
        ["universes/vael/series/a/books/a2/book.md"] = "---\nname: Book 2\nstatus: final\nchapters: [1]\n---\n",
        ["universes/vael/series/a/books/a1/entities/characters/orrin-fell.md"] =
            "---\r\n# Orrin's notes\r\ntype: character\r\nname: Orrin Fell\r\nstatus: canon\r\n---\r\n\r\n## Overview\r\nA deckhand.\r\n",
        ["universes/vael/series/a/books/a1/entities/characters/zoe-marr.md"] =
            "---\nid: 01JC5CK2000000000000000005\ntype: character\nname: Zoë Marr\n---\n",
        ["universes/vael/series/a/books/a1/entities/characters/torn.md"] = "---\ntype: character\nname: [Torn\n---\n",
        ["universes/vael/series/a/books/a2/entities/characters/the-stranger.md"] = "---\ntype: character\nname: The Stranger\n---\n",
    };

    [Fact]
    [Trait("Scenario", "SC-CLI-4")]
    [Trait("Scenario", "SC-CHECK-32")]
    public void FixAddsMissingIdsAndReportsEachFile()
    {
        using var den = TempDen.FromFiles(NoIdsDen);
        var before = Check(den.Root);
        Assert.Contains("orrin-fell.md:1: warning F-ID-2: no id; run `wolfden check --fix` to add one.", before.Output, StringComparison.Ordinal);
        // In a final book, --fix won't help, so the warning doesn't suggest it.
        Assert.Contains(
            "the-stranger.md:1: warning F-ID-2: no id, and book 'a2' is final (frozen), so `wolfden check --fix` won't add one until the book is unfrozen.",
            before.Output,
            StringComparison.Ordinal);

        // Container files get the same warning (#100), and the same wording in a final book.
        Assert.Contains(
            "universes/vael/universe.md:1: warning F-ID-2: no id; run `wolfden check --fix` to add one. Until then, the index knows this universe by its path.",
            before.Output,
            StringComparison.Ordinal);
        Assert.Contains("universes/vael/series/a/books/a1/book.md:1: warning F-ID-2: no id; run `wolfden check --fix` to add one.", before.Output, StringComparison.Ordinal);
        Assert.Contains(
            "universes/vael/series/a/books/a2/book.md:1: warning F-ID-2: no id, and book 'a2' is final (frozen), so `wolfden check --fix` won't add one until the book is unfrozen. Until then, the index knows this book by its path.",
            before.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("series.md:1: warning F-ID-2", before.Output, StringComparison.Ordinal);

        var (exitCode, output, error) = Check(den.Root, "--fix");

        Assert.Empty(error);
        Assert.Equal(CliApp.ExitErrors, exitCode); // torn.md still has its error
        var lines = output.Split('\n');
        Assert.Matches(@"^universes/vael/series/a/books/a1/book\.md: fixed F-ID-2: added id [0-7][0-9A-HJKMNP-TV-Z]{25}$", lines[0]);
        Assert.Matches(@"^universes/vael/series/a/books/a1/entities/characters/orrin-fell\.md: fixed F-ID-2: added id ", lines[1]);
        Assert.Equal(
            "universes/vael/series/a/books/a2/book.md: not fixed F-ID-2: it's in book 'a2', which is final (frozen), so it can't be edited until the book is unfrozen (F-BOOK-1).",
            lines[2]);
        Assert.StartsWith("universes/vael/series/a/books/a2/entities/characters/the-stranger.md: not fixed F-ID-2: it's in book 'a2'", lines[3], StringComparison.Ordinal);
        Assert.Matches(@"^universes/vael/universe\.md: fixed F-ID-2: added id ", lines[4]);
        Assert.Equal("", lines[5]);
        Assert.DoesNotContain("orrin-fell.md:1: warning F-ID-2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("universe.md:1: warning F-ID-2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a1/book.md:1: warning F-ID-2", output, StringComparison.Ordinal);
        Assert.Contains("a2/book.md:1: warning F-ID-2: no id, and book 'a2' is final (frozen)", output, StringComparison.Ordinal);

        // Only the id line is new; the comment line stays first, and CRLF stays CRLF.
        var id = lines[1][^26..];
        Assert.Equal(
            $"---\r\n# Orrin's notes\r\nid: {id}\r\ntype: character\r\nname: Orrin Fell\r\nstatus: canon\r\n---\r\n\r\n## Overview\r\nA deckhand.\r\n",
            den.Read("universes/vael/series/a/books/a1/entities/characters/orrin-fell.md"));
        Assert.StartsWith("---\nid: ", den.Read("universes/vael/universe.md"), StringComparison.Ordinal);
        Assert.EndsWith("\nname: Vael\norder: [a]\n---\n\nThe world.\n", den.Read("universes/vael/universe.md"), StringComparison.Ordinal);

        // Files that were skipped, already had an id, or have an error are untouched.
        foreach (var path in new[]
        {
            "universes/vael/series/a/books/a2/book.md",
            "universes/vael/series/a/books/a2/entities/characters/the-stranger.md",
            "universes/vael/series/a/series.md",
            "universes/vael/series/a/books/a1/entities/characters/zoe-marr.md",
            "universes/vael/series/a/books/a1/entities/characters/torn.md",
        })
        {
            Assert.Equal(NoIdsDen[path], den.Read(path));
        }
    }

    [Fact]
    public void FixIsReportedInJsonAndASecondRunHasNothingToDo()
    {
        using var den = TempDen.FromFiles(NoIdsDen);

        var (_, output, _) = Check(den.Root, "--fix", "--json");
        var (_, again, _) = Check(den.Root, "--fix", "--json");

        using var json = JsonDocument.Parse(output);
        var fixes = json.RootElement.GetProperty("fixes").EnumerateArray().ToList();
        Assert.Equal(5, fixes.Count);
        Assert.Equal(["path", "rule", "outcome", "id", "message"], fixes[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, fixes.Count(f => f.GetProperty("outcome").GetString() == "fixed"));
        Assert.All(fixes.Where(f => f.GetProperty("outcome").GetString() == "skipped"), f => Assert.Equal(JsonValueKind.Null, f.GetProperty("id").ValueKind));
        using var second = JsonDocument.Parse(again);
        Assert.All(second.RootElement.GetProperty("fixes").EnumerateArray(), f => Assert.Equal("skipped", f.GetProperty("outcome").GetString()));
    }

    [Fact]
    public void FixOnACleanDenChangesNothing()
    {
        using var den = TempDen.DraculaCopy();
        var before = Cli.Snapshot(den.Root);

        var (exitCode, output, _) = Check(den.Root, "--fix");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal("No errors or warnings. Checked 50 entities; spoiler checks are on.\n", output);
        Assert.Equal(before, Cli.Snapshot(den.Root));
    }
}

/// <summary>Tests that change the process's current folder, so they don't run alongside others.</summary>
[CollectionDefinition(nameof(CurrentFolder), DisableParallelization = true)]
public sealed class CurrentFolder;

[Collection(nameof(CurrentFolder))]
public class CheckCommandCurrentFolderTests
{
    [Fact]
    public void WithoutAFolderItChecksTheCurrentOne()
    {
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = TempDen.DraculaRoot;
            var output = new StringWriter();

            var exitCode = CliApp.Run(["check"], output, output);

            Assert.Equal(CliApp.ExitOk, exitCode);
            Assert.StartsWith("No errors or warnings. Checked 50 entities", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-5")]
    public void FromAFolderInsideADenItFindsTheDenAbove()
    {
        var inside = Path.Combine(TempDen.DraculaRoot, "universes", "dracula", "books", "dracula", "entities", "characters");

        var (exitCode, output, _) = Cli.RunIn(inside, "check");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.StartsWith("No errors or warnings. Checked 50 entities", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideAnyDenItReportsThatTheCurrentFolderIsntOne()
    {
        using var empty = TempDen.Empty();

        var (exitCode, output, _) = Cli.RunIn(empty.Root, "check");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("den.yaml: error F-DEN-1: This folder has no den.yaml", output, StringComparison.Ordinal);
    }
}
