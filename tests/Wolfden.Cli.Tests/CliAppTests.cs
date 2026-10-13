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
    public void NoArgumentsPrintsUsageNamingThePlannedCommands()
    {
        var output = new StringWriter();

        var exitCode = CliApp.Run([], output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.StartsWith("usage: wolfden", text);
        Assert.Contains("not yet implemented", text);
        foreach (var command in new[] { "init", "index", "check", "query", "serve", "mcp" })
        {
            Assert.Contains(command, text);
        }
    }

    [Fact]
    public void UnknownCommandPrintsUsageAndFails()
    {
        var output = new StringWriter();

        var exitCode = CliApp.Run(["init"], output);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("usage: wolfden", output.ToString());
    }
}
