using System.Runtime.Versioning;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>check</c>, <c>index</c>, and <c>query</c> don't fail on a note they
/// can't read: it's left out, as if it weren't there (#112). The index
/// tests cover broken links and the watcher.
/// </summary>
public sealed class UnreadableFileTests
{
    private const string Jonathan = "universes/dracula/books/dracula/entities/characters/jonathan-harker.md";

    [Fact]
    [Trait("Scenario", "SC-CLI-8")]
    public void ANoteThatCantBeReadIsLeftOutAndTheCommandsStillWork()
    {
        using var den = TempDen.DraculaCopy();
        using var deleted = TempDen.DraculaCopy();
        File.Delete(Full(deleted.Root, Jonathan));
        var (expectedExit, expected, _) = Cli.Run("check", deleted.Root);

        using (Unreadable(Full(den.Root, Jonathan)))
        {
            var (checkExit, check, checkError) = Cli.Run("check", den.Root);
            Assert.Equal(expectedExit, checkExit);
            Assert.Equal(expected.Replace(deleted.Root, "<den>", StringComparison.Ordinal), check.Replace(den.Root, "<den>", StringComparison.Ordinal));
            Assert.Empty(checkError);

            var (indexExit, _, indexError) = Cli.Run("index", "--den", den.Root);
            Assert.Equal(CliApp.ExitOk, indexExit);
            Assert.Empty(indexError);

            var (queryExit, query, queryError) = Cli.Run("query", "--den", den.Root, "--at", "dracula:12", "--brief", "--type", "character");
            Assert.Equal(CliApp.ExitOk, queryExit);
            Assert.Empty(queryError);
            Assert.DoesNotContain(Jonathan, query, StringComparison.Ordinal);
        }

        // Once it can be read again, the next query sees it.
        var (_, after, _) = Cli.Run("query", "--den", den.Root, "--at", "dracula:12", "--brief", "--type", "character");
        Assert.Contains(Jonathan, after, StringComparison.Ordinal);
    }

    private static string Full(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Makes the file at <paramref name="full"/> unreadable until disposed:
    /// held open with no sharing on Windows, or with no permissions
    /// elsewhere. Skips the test where that doesn't stop reads, as for root.
    /// </summary>
    private static IDisposable Unreadable(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        var cleanup = WithoutPermissions(full);
        try
        {
            File.ReadAllBytes(full);
        }
        catch (UnauthorizedAccessException)
        {
            return cleanup;
        }

        cleanup.Dispose();
        Assert.Skip("This user can read a file without read permission.");
        return null!;
    }

    [UnsupportedOSPlatform("windows")]
    private static Cleanup WithoutPermissions(string full)
    {
        var mode = File.GetUnixFileMode(full);
        File.SetUnixFileMode(full, UnixFileMode.None);
        return new Cleanup(() => File.SetUnixFileMode(full, mode));
    }

    private sealed class Cleanup(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
