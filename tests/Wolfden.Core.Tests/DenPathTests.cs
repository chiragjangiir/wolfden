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
    [InlineData("universes/starfall/entities/characters/caf\u00E9-owner.md")]
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
    [InlineData("universes/starfall/com\u00B9.md", "device name")]
    [InlineData("universes/starfall/conin$", "device name")]
    [InlineData("universes/starfall/prn .md", "device name")]
    public void ANameWindowsDoesntKeepAsWrittenIsRefusedOnEveryOs(string path, string why)
    {
        var problem = DenPaths.Problem(path);

        Assert.NotNull(problem);
        Assert.Contains(why, problem, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> DisguisedNames() => new()
    {
        // Characters that only disguise a name, anywhere in it.
        { "mara\u200B-voss.md", "disguises" },
        { "mara\uFEFF-voss.md", "disguises" },
        { "mara\u200E-voss.md", "disguises" },
        { "mara\u200F-voss.md", "disguises" },
        { "mara\u202E-voss.md", "disguises" },
        { "mara\u2066-voss.md", "disguises" },
        { "mara\u061C-voss.md", "disguises" },
        // Invisible at the start or the end.
        { "\u00ADmara-voss.md", "starts or ends" },
        { "mara-voss.md\u2060", "starts or ends" },
        { "mara-voss.md\u200D", "starts or ends" },
        // Empty or hidden once the invisible characters are left out.
        { "\u200D\u200C", "looks empty" },
        { "\u200D.mara-voss.md", "looks hidden" },
    };

    [Theory]
    [MemberData(nameof(DisguisedNames))]
    [Trait("Scenario", "SC-STORE-3")]
    public void ANewNameThatInvisibleCharactersDisguiseIsRefused(string name, string why)
    {
        var problem = DenPaths.NewNameProblem(name);

        Assert.NotNull(problem);
        Assert.Contains(why, problem, StringComparison.Ordinal);
        Assert.Equal(DenStoreError.InvalidPath, Assert.Throws<DenStoreException>(() => DenPaths.CheckNewName($"universes/starfall/{name}", name)).Error);
    }

    public static TheoryData<string> RealNames() => new()
    {
        "\u2764\uFE0F-notes.md",
        "family-\U0001F468\u200D\U0001F469\u200D\U0001F467.md",
        "\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645.md",
        "\u0915\u094D\u200C\u0937.md",
        "\u2764\uFE0F",
        "flag-\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F",
        "re\u00ADmember.md",
        "\U0001F680-rocket.md",
    };

    [Theory]
    [MemberData(nameof(RealNames))]
    [Trait("Scenario", "SC-STORE-3")]
    public void EmojiAndScriptsThatNeedJoinersAreFine(string name)
    {
        // An emoji's variation selector and joiners, Persian and Indic
        // non-joiners, a soft hyphen inside a word: as git allows them.
        Assert.Null(DenPaths.NewNameProblem(name));
        Assert.Null(DenPaths.Problem($"universes/starfall/{name}"));
    }

    [Fact]
    public void AnExistingNameIsntCheckedForInvisibleCharacters()
    {
        // Problem checks every name on a path, old ones too; only new names are checked for these.
        Assert.Null(DenPaths.Problem("universes/starfall/mara\u200B-voss.md"));
    }

    [Fact]
    public void ANewNameThatIsntValidTextIsRefused()
    {
        Assert.Contains("isn't valid text", DenPaths.NewNameProblem("mara\uD800.md"), StringComparison.Ordinal);
        Assert.Contains("isn't valid text", DenPaths.NewNameProblem("mara\uDC00.md"), StringComparison.Ordinal);
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
