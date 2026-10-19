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
        Assert.Contains("check [<den>] [--json]", text);
        Assert.Contains("not yet implemented: init, index, query, serve, mcp", text);
    }

    [Fact]
    public void APlannedCommandSaysItIsntImplementedAndExitsTwo()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CliApp.Run(["init"], output, error);

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith("wolfden: 'init' isn't implemented yet.", error.ToString());
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
}
