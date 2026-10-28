namespace Wolfden.Cli.Tests;

public class CliAppTests
{
    [Fact]
    public void VersionFlagPrintsTheVersion()
    {
        var output = new StringWriter();

        var exitCode = CliApp.Run(["--version"], output);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("0.1.0-dev", output.ToString().Trim());
    }

    [Fact]
    public void NoArgumentsPrintsUsageNamingTheCommands()
    {
        var output = new StringWriter();

        var exitCode = CliApp.Run([], output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.StartsWith("usage: wolfden", text);
        foreach (var command in CliApp.Commands)
        {
            Assert.Contains($"\n  {command} ", text.ReplaceLineEndings("\n"));
        }

        Assert.DoesNotContain("not yet implemented", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("check")]
    [InlineData("index")]
    [InlineData("query")]
    [InlineData("copy")]
    [InlineData("freeze")]
    [InlineData("unfreeze")]
    [InlineData("new-id")]
    [InlineData("serve")]
    [InlineData("mcp")]
    public void EveryCommandHasHelpThatStartsWithItsUsage(string command)
    {
        var (exitCode, output, error) = Cli.Run(command, "--help");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Empty(error);
        Assert.StartsWith($"usage: wolfden {command}", output, StringComparison.Ordinal);
        Assert.Equal(output, Cli.Run("help", command).Output);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("index")]
    [InlineData("query")]
    [InlineData("copy")]
    [InlineData("freeze")]
    [InlineData("unfreeze")]
    public void EveryCommandThatReadsADenTakesDenAndJson(string command)
    {
        var (_, output, _) = Cli.Run(command, "--help");

        Assert.Contains("--den <den>", output, StringComparison.Ordinal);
        Assert.Contains("--json", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandPrintsUsageAndExitsTwo()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CliApp.Run(["chek"], output, error);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith("wolfden: unknown command 'chek'.", error.ToString());
        Assert.Contains("usage: wolfden", error.ToString());
    }

    [Theory]
    [InlineData("query", "--at")]
    [InlineData("init", "--name")]
    public void AnOptionWithoutItsValueExitsTwo(string command, string option)
    {
        var (exitCode, output, error) = Cli.Run(command, option);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output);
        Assert.StartsWith($"wolfden {command}: '{option}' needs a value.\nusage: wolfden {command}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionGivenTwiceExitsTwoUnlessItRepeats()
    {
        var (exitCode, _, error) = Cli.Run("query", "--at", "a1:1", "--at=a1:2");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden query: give '--at' only once.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoubleDashMakesTheRestPlainArguments()
    {
        var (exitCode, output, _) = Cli.Run("new-id", "--", "2");
        var (rejected, _, error) = Cli.Run("new-id", "--", "--json");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal(2, output.TrimEnd('\n').Split('\n').Length);
        Assert.Equal(CliApp.ExitUsage, rejected);
        Assert.StartsWith("wolfden new-id: the count must be a whole number from 1 to 1000, not '--json'.", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueOptionTakesItsValueAfterAnEqualsSignOrAsTheNextArgument()
    {
        using var folder = TempDen.Empty();

        var (byEquals, _, _) = Cli.Run("init", Path.Combine(folder.Root, "a"), "--name=Notes A");
        var (bySpace, _, _) = Cli.Run("init", Path.Combine(folder.Root, "b"), "--name", "Notes A");

        Assert.Equal(CliApp.ExitOk, byEquals);
        Assert.Equal(CliApp.ExitOk, bySpace);
        Assert.Equal(File.ReadAllText(Path.Combine(folder.Root, "a", "den.yaml")), File.ReadAllText(Path.Combine(folder.Root, "b", "den.yaml")));
        Assert.Contains("name: Notes A\n", File.ReadAllText(Path.Combine(folder.Root, "a", "den.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagWithAValueExitsTwo()
    {
        var (exitCode, _, error) = Cli.Run("check", "--json=yes");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.StartsWith("wolfden check: '--json' doesn't take a value.", error, StringComparison.Ordinal);
    }
}
