namespace Wolfden.Cli.Tests;

/// <summary><c>wolfden check --universe</c>: one universe's files and the den-level ones (maintainer decision, 2026-10-23).</summary>
public sealed class CheckUniverseTests : IDisposable
{
    private readonly TempDen _den = TempDen.FromFiles(new Dictionary<string, string>
    {
        ["den.yaml"] = "format: 1\n",
        ["universes/vael/universe.md"] = "---\nid: 01JC5CK3000000000000000001\nname: Vael\n---\n",
        ["universes/orn/universe.md"] = "---\nid: 01JC5CK3000000000000000002\nname: Orn\n---\n",
        ["universes/vael/entities/characters/a.md"] = "---\nid: 01JC5CK3000000000000000011\ntype: character\nname: A\n---\n\nSee [[Nobody]].\n",
        ["universes/orn/entities/characters/b.md"] = "---\nid: 01JC5CK3000000000000000012\ntype: character\nname: B\n---\n\nSee [[Nobody Else]].\n",
    });

    public void Dispose() => _den.Dispose();

    [Fact]
    [Trait("Scenario", "SC-CLI-11")]
    public void WithAUniverseOnlyItsFindingsAreReported()
    {
        var (whole, wholeOutput, _) = Cli.Run("check", _den.Root);
        var (vael, vaelOutput, _) = Cli.Run("check", _den.Root, "--universe", "vael");
        var (star, starOutput, _) = Cli.Run("check", _den.Root, "--universe", "*");

        Assert.Contains("universes/vael/entities/characters/a.md", wholeOutput, StringComparison.Ordinal);
        Assert.Contains("universes/orn/entities/characters/b.md", wholeOutput, StringComparison.Ordinal);

        Assert.NotEqual(CliApp.ExitUsage, vael);
        Assert.Contains("universes/vael/entities/characters/a.md", vaelOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("universes/orn/", vaelOutput, StringComparison.Ordinal);
        Assert.Contains("Checked 1 entity", vaelOutput, StringComparison.Ordinal);

        // '*' is the whole den.
        Assert.Equal(whole, star);
        Assert.Equal(wholeOutput, starOutput);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-11")]
    public void ABrokenDenYamlIsStillReported()
    {
        _den.Write("den.yaml", "format: 1\nname: [broken\n");

        var (exitCode, output, _) = Cli.Run("check", _den.Root, "--universe", "vael");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Contains("den.yaml", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownUniverseOrFixWithAUniverseExitsTwo()
    {
        var (unknown, _, error) = Cli.Run("check", _den.Root, "--universe", "nowhere");
        Assert.Equal(CliApp.ExitUsage, unknown);
        Assert.Equal("wolfden check: the den has no universe 'nowhere'. Its universes: orn, vael.\n", error);

        var (fix, _, fixError) = Cli.Run("check", _den.Root, "--universe", "vael", "--fix");
        Assert.Equal(CliApp.ExitUsage, fix);
        Assert.StartsWith("wolfden check: '--fix' adds ids across the whole den", fixError, StringComparison.Ordinal);
    }
}
