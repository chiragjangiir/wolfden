using System.Text.Json;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>wolfden freeze</c> and <c>wolfden unfreeze</c> (#31): the command
/// line over <c>DenTools.FreezeBook</c> and <c>UnfreezeBook</c>, whose
/// behaviour is tested in Wolfden.Mcp (<c>FreezeBookTests</c>). Each test
/// works on its own copy of <c>samples/dracula</c>.
/// </summary>
public sealed class FreezeCommandTests : IDisposable
{
    private const string BookFile = "universes/dracula/books/dracula/book.md";
    private const string Tag = "book/dracula/dracula/final";

    private readonly TempDen _den = TempDen.DraculaCopy();

    public void Dispose() => _den.Dispose();

    private (int ExitCode, string Output, string Error) Run(params string[] args) =>
        Cli.Run([args[0], "--den", _den.Root, .. args[1..]]);

    [Fact]
    [Trait("Scenario", "SC-CLI-14")]
    public void WithoutHistoryFreezeMarksTheBookFinalAndUnfreezeWritesDraft()
    {
        var (exitCode, output, error) = Run("freeze", "dracula");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.StartsWith("Froze book 'Dracula': book.md now says status: final. No snapshot was taken: This den has no history", output, StringComparison.Ordinal);
        Assert.Contains("\nstatus: final\n", _den.Read(BookFile), StringComparison.Ordinal);

        // A copy into the frozen book is refused now.
        Assert.Contains("final (frozen)", Cli.Run("copy", "--den", _den.Root, "Mr. Swales", "Young Swales").Error, StringComparison.Ordinal);

        var (unfreezeCode, unfrozen, _) = Run("unfreeze", "dracula", "--json");

        Assert.Equal(CliApp.ExitOk, unfreezeCode);
        var json = JsonDocument.Parse(unfrozen).RootElement;
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal("draft", json.GetProperty("status").GetString());
        Assert.Equal("no_history", json.GetProperty("snapshot").GetString());
        Assert.Contains("\nstatus: draft\n", _den.Read(BookFile), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-14")]
    public void WithGitFreezeCheckpointsAndTagsAndUnfreezeCheckpoints()
    {
        TestGit.Run(_den.Root, "init", "--quiet");
        TestGit.Run(_den.Root, "add", "--all");
        TestGit.Run(_den.Root, "commit", "--quiet", "-m", "Start");

        var (exitCode, output, error) = Run("freeze", "dracula", "--universe", "dracula");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.EndsWith($"Tagged the snapshot '{Tag}'.\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(TestGit.Run(_den.Root, "rev-parse", "HEAD"), TestGit.Run(_den.Root, "rev-parse", Tag + "^{commit}"));

        Assert.Equal(CliApp.ExitOk, Run("unfreeze", "dracula").ExitCode);
        Assert.Equal("Unfreeze book 'dracula' of universe 'dracula'\n", TestGit.Run(_den.Root, "log", "-1", "--format=%s"));

        Assert.Equal(CliApp.ExitOk, Run("freeze", "dracula").ExitCode);
        Assert.Equal($"{Tag}\n{Tag}-2\n", TestGit.Run(_den.Root, "tag", "--list"));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-14")]
    public void AFailedCheckpointExitsTwoAndSaysToFreezeAgain()
    {
        TestGit.Run(_den.Root, "init", "--quiet");
        TestGit.Run(_den.Root, "add", "--all");
        TestGit.Run(_den.Root, "commit", "--quiet", "-m", "Start");
        var hook = Path.Combine(_den.Root, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var (exitCode, output, error) = Run("freeze", "dracula");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith("wolfden freeze: Book 'Dracula' is final in book.md now, but the checkpoint failed", error, StringComparison.Ordinal);
        Assert.Contains("freeze the book again", error, StringComparison.Ordinal);
        Assert.Contains("\nstatus: final\n", _den.Read(BookFile), StringComparison.Ordinal);

        File.Delete(hook);
        Assert.Equal(CliApp.ExitOk, Run("freeze", "dracula").ExitCode);
        Assert.Equal($"{Tag}\n", TestGit.Run(_den.Root, "tag", "--list"));
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-14")]
    public void AnUnknownBookOrNoBookExitsTwo()
    {
        var (exitCode, _, error) = Run("freeze", "sequel");
        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Contains("has no book 'sequel'", error, StringComparison.Ordinal);

        var (none, _, noneError) = Run("unfreeze");
        Assert.Equal(CliApp.ExitUsage, none);
        Assert.StartsWith("wolfden unfreeze: give the book's key.", noneError, StringComparison.Ordinal);

        Assert.Equal(CliApp.ExitUsage, Run("freeze", "a", "b").ExitCode);
    }
}
