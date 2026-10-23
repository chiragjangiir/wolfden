using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// The paths a den store writes (#121): den-relative, unable to leave the
/// den, not hidden, and the same file on every OS, so a den written on
/// Linux loads on Windows (<see cref="DenPaths"/>).
/// </summary>
public sealed class DenPathTests
{
    [Theory]
    [InlineData("den.yaml")]
    [InlineData("universes/starfall/universe.md")]
    [InlineData("universes/starfall/entities/characters/mara-voss.md")]
    [InlineData("universes/starfall/entities/characters/café-owner.md")]
    [InlineData("universes/starfall/entities/characters/con-artist.md")]
    [InlineData("universes/starfall/entities/characters/console.md")]
    [InlineData("universes/starfall/entities/items/com10.md")]
    [InlineData("universes/starfall/entities/items/a.b.c.md")]
    [InlineData("universes/starfall/entities/items/the lantern.md")]
    [InlineData("universes/starfall/entities/items/~lantern.md")]
    public void APathInTheDenCanBeWritten(string path)
    {
        Assert.Null(DenPaths.Problem(path));
        DenPaths.CheckWritable(path);
    }

    [Theory]
    [Trait("Scenario", "SC-STORE-2")]
    [InlineData("", "empty")]
    [InlineData("..", "outside the den")]
    [InlineData("../outside.md", "outside the den")]
    [InlineData("universes/../../outside.md", "outside the den")]
    [InlineData("universes/./universe.md", "outside the den")]
    [InlineData("/etc/passwd", "relative to the den")]
    [InlineData("//server/share/x.md", "relative to the den")]
    [InlineData("C:/Windows/win.ini", "Windows doesn't allow")]
    [InlineData("C:outside.md", "Windows doesn't allow")]
    [InlineData("..\\outside.md", "use '/'")]
    [InlineData("universes\\starfall\\universe.md", "use '/'")]
    [InlineData("\\\\server\\share\\x.md", "use '/'")]
    [InlineData("universes//universe.md", "empty")]
    [InlineData("universes/", "empty")]
    [InlineData(".git/config", "hidden")]
    [InlineData(".wolfden/index.db", "hidden")]
    [InlineData("universes/starfall/.obsidian/workspace.json", "hidden")]
    [InlineData("universes/starfall/entities/.mara.md.swp", "hidden")]
    public void APathThatCouldLeaveTheDenOrIsHiddenIsRefused(string path, string why)
    {
        var problem = DenPaths.Problem(path);

        Assert.NotNull(problem);
        Assert.Contains(why, problem, StringComparison.Ordinal);
        var e = Assert.Throws<DenStoreException>(() => DenPaths.CheckWritable(path));
        Assert.Equal(DenStoreError.InvalidPath, e.Error);
        Assert.Equal(path, e.Path);
    }

    [Theory]
    [Trait("Scenario", "SC-STORE-2")]
    [InlineData("universes/starfall/mara.md:hidden", "Windows doesn't allow")]
    [InlineData("universes/starfall/mara.md::$DATA", "Windows doesn't allow")]
    [InlineData("universes/star<fall/x.md", "Windows doesn't allow")]
    [InlineData("universes/star>fall/x.md", "Windows doesn't allow")]
    [InlineData("universes/star\"fall/x.md", "Windows doesn't allow")]
    [InlineData("universes/star|fall/x.md", "Windows doesn't allow")]
    [InlineData("universes/star?fall/x.md", "Windows doesn't allow")]
    [InlineData("universes/star*fall/x.md", "Windows doesn't allow")]
    [InlineData("universes/star\tfall/x.md", "control character")]
    [InlineData("universes/star\0fall/x.md", "control character")]
    [InlineData("universes/starfall/mara.md.", "drops")]
    [InlineData("universes/starfall/mara.md ", "drops")]
    [InlineData("universes/starfall./universe.md", "drops")]
    [InlineData("con", "device name")]
    [InlineData("universes/CON/universe.md", "device name")]
    [InlineData("universes/starfall/nul.md", "device name")]
    [InlineData("universes/starfall/Aux.tar.gz", "device name")]
    [InlineData("universes/starfall/com1.md", "device name")]
    [InlineData("universes/starfall/LPT9.md", "device name")]
    [InlineData("universes/starfall/com¹.md", "device name")]
    [InlineData("universes/starfall/conin$", "device name")]
    [InlineData("universes/starfall/prn .md", "device name")]
    public void ANameWindowsDoesntKeepAsWrittenIsRefusedOnEveryOs(string path, string why)
    {
        var problem = DenPaths.Problem(path);

        Assert.NotNull(problem);
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-STORE-3")]
    public void ANameWithAnInvisibleCharacterIsRefused()
    {
        // Each looks like mara-voss.md, but isn't the same name.
        foreach (var invisible in new[] { "​", "‍", "­", "﻿", "⁠", "‎", "‮", "️", "\U000E0041" })
        {
            var problem = DenPaths.Problem($"universes/starfall/mara{invisible}-voss.md");

            Assert.NotNull(problem);
            Assert.Contains("invisible character", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ANameThatIsntValidTextIsRefused()
    {
        Assert.Contains("isn't valid text", DenPaths.Problem("universes/starfall/mara\uD800.md"), StringComparison.Ordinal);
        Assert.Contains("isn't valid text", DenPaths.Problem("universes/starfall/mara\uDC00.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ACharacterOutsideTheBasicPlaneIsFine()
    {
        Assert.Null(DenPaths.Problem("universes/starfall/entities/items/\U0001F680-rocket.md"));
    }

    [Theory]
    [InlineData("a/b/c.md", "a/b", "c.md")]
    [InlineData("den.yaml", "", "den.yaml")]
    public void ParentAndName(string path, string parent, string name)
    {
        Assert.Equal(parent, DenPaths.Parent(path));
        Assert.Equal(name, DenPaths.Name(path));
    }

    [Theory]
    [InlineData("a/b", "a", true)]
    [InlineData("a", "a", true)]
    [InlineData("A/b", "a", true)]
    [InlineData("ab", "a", false)]
    [InlineData("b/a", "a", false)]
    public void AtOrInside(string path, string folder, bool inside)
    {
        Assert.Equal(inside, DenPaths.IsAtOrInside(path, folder));
    }
}
