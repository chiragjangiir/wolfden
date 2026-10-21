using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Wolfden.Core.Structure;

namespace Wolfden.Cli.Tests;

/// <summary>
/// <c>check</c>, <c>index</c>, and <c>query</c> don't fail on a folder the
/// user may not list: it's left out, as if it weren't there (#116). The
/// index tests cover container folders, a stale index, and the watcher.
/// </summary>
public sealed class UnlistableFolderTests
{
    private const string Characters = "universes/dracula/books/dracula/entities/characters";
    private const string Jonathan = "universes/dracula/books/dracula/entities/characters/jonathan-harker.md";

    [Fact]
    [Trait("Scenario", "SC-CLI-9")]
    public void AFolderThatCantBeListedIsLeftOutAndTheCommandsStillWork()
    {
        using var den = TempDen.DraculaCopy();
        using var deleted = TempDen.DraculaCopy();
        Directory.Delete(Full(deleted.Root, Characters), recursive: true);
        var (expectedExit, expected, _) = Cli.Run("check", deleted.Root);

        using (Unlistable(Full(den.Root, Characters)))
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

        // Once it can be listed again, the next query sees what's in it.
        var (_, after, _) = Cli.Run("query", "--den", den.Root, "--at", "dracula:12", "--brief", "--type", "character");
        Assert.Contains(Jonathan, after, StringComparison.Ordinal);
    }

    private static string Full(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Makes the folder at <paramref name="full"/> one the user may not list
    /// until disposed: a deny rule for listing on Windows, or no permissions
    /// elsewhere. Skips the test where that doesn't stop listing, as for
    /// root. Disposing it restores the permissions, so the temporary den can
    /// be deleted.
    /// </summary>
    private static IDisposable Unlistable(string full)
    {
        var cleanup = OperatingSystem.IsWindows() ? DenyListing(full) : WithoutPermissions(full);
        if (!FileSystemDenSource.CanList(full))
        {
            return cleanup;
        }

        cleanup.Dispose();
        Assert.Skip("This user can list a folder without permission to list it.");
        return null!;
    }

    [SupportedOSPlatform("windows")]
    private static Cleanup DenyListing(string full)
    {
        var folder = new DirectoryInfo(full);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(rule);
        folder.SetAccessControl(security);
        return new Cleanup(() =>
        {
            var restored = folder.GetAccessControl();
            restored.RemoveAccessRuleSpecific(rule);
            folder.SetAccessControl(restored);
        });
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
