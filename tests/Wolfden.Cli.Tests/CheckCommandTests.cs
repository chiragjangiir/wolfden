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
        [$"{A1}/entities/characters/Old Notes.md"] = "---\nid: 01JC5CK1000000000000000007\ntype: note\nname: Old notes\nstatus: draft\n---\n",
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
            {A1}/entities/characters/Old Notes.md: warning F-KEY-4: The file name 'Old Notes.md' isn't a valid slug (lowercase letters and digits, with single hyphens between words), so links reach this entity only by id, name, or alias.
            {A1}/entities/characters/orrin-fell.md:9: warning F-POS-4: a1:9: Book 'a1' has no chapter '9'. Until it resolves, the fact that uses it is left out of canon views.
            {A1}/entities/characters/orrin-fell.md:13: warning F-LINK-13: [[the-stranger]]: 'The Stranger' enters canon at the start of book 'a2', after this link's position, the start of book 'a1'. It may be a spoiler.
            {A1}/entities/characters/orrin-fell.md:13: warning F-LINK-8: [[Zoë Marr]]: 'Zoë Marr' matches no entity in universe 'vael'.
            {A1}/entities/characters/torn.md:5: error F-YAML-1: {ErrorMessage(den)}

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
    }

    [Fact]
    public void JsonOutputHasAStableShape()
    {
        using var den = TempDen.FromFiles(MixedDen);

        var (_, output, _) = Check("--json", den.Root);

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(["version", "den", "summary", "diagnostics"], root.EnumerateObject().Select(p => p.Name));
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
    [InlineData("--fix")]
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
        Assert.Equal("usage: wolfden check [<den>] [--json]\n", output);
    }

    [Fact]
    public void CheckNeverWritesToTheDen()
    {
        using var den = TempDen.FromFiles(MixedDen);
        var before = Snapshot(den.Root);

        Check(den.Root);
        Check(den.Root, "--json");

        Assert.Equal(before, Snapshot(den.Root));
    }

    /// <summary>Every file and folder, with each file's SHA-256 and last write time, so an in-place rewrite shows too.</summary>
    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{p} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))} {File.GetLastWriteTimeUtc(p).Ticks}"
                : p)
            .ToList();
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
}
