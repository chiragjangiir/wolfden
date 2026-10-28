using System.Diagnostics;
using System.Text;
using Wolfden.Core.History;
using Wolfden.Git;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// Freezing and unfreezing a book (#31; design.md §5, F-BOOK-1): the
/// writer's operation behind the web UI and the CLI, over a copy of the
/// Dracula sample, with and without git. Git runs with the tests' own
/// configuration, never the developer's.
/// </summary>
public sealed class FreezeBookTests : IDisposable
{
    private const string BookFile = "universes/dracula/books/dracula/book.md";
    private const string LucyFile = "universes/dracula/books/dracula/entities/characters/lucy-westenra.md";
    private const string Tag = "book/dracula/dracula/final";

    private readonly TempDen _den = TempDen.Dracula();
    private readonly string _config;

    public FreezeBookTests()
    {
        _config = Path.Combine(Path.GetTempPath(), "wolfden-mcp-tests", $"gitconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(_config)!);
        File.WriteAllText(
            _config,
            "[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[tag]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n",
            new UTF8Encoding(false));
    }

    private GitOptions Git => new()
    {
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = _config,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CEILING_DIRECTORIES"] = Path.Combine(Path.GetTempPath(), "wolfden-mcp-tests"),
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
        },
    };

    private DenTools WithGit() => new(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void WithoutHistoryFreezingIsStatusFinalAloneAndSaysNoSnapshotWasTaken()
    {
        var tools = new DenTools(_den.Root);
        var before = _den.Read(BookFile);
        var status = tools.GetBookFreeze("dracula");
        Assert.Equal("draft", status.Status);
        Assert.False(status.History);
        Assert.Contains("no history", status.Reason, StringComparison.Ordinal);
        Assert.Null(status.NextTag);

        var frozen = tools.FreezeBook("dracula", status.Hash);

        Assert.True(frozen.Changed);
        Assert.Equal("final", frozen.Status);
        Assert.Equal(FreezeSnapshot.NoHistory, frozen.Snapshot);
        Assert.Null(frozen.Checkpoint);
        Assert.Null(frozen.Tag);
        Assert.StartsWith("Froze book 'Dracula': book.md now says status: final. No snapshot was taken: This den has no history", frozen.Message, StringComparison.Ordinal);

        // Edited in place: only the status line changed.
        Assert.Equal(before.Replace("status: draft\n", "status: final\n", StringComparison.Ordinal), _den.Read(BookFile));

        // The book's notes are frozen now.
        var lucy = tools.GetEntity("Lucy Westenra").Entity!;
        Assert.Equal(ToolErrorCodes.Frozen, Assert.Throws<DenToolException>(() => tools.AppendSection("Lucy Westenra", "Overview", "More.", lucy.Hash!)).Code);

        var unfrozen = tools.UnfreezeBook("dracula", frozen.Hash);

        Assert.True(unfrozen.Changed);
        Assert.Equal("draft", unfrozen.Status);
        Assert.Equal(FreezeSnapshot.NoHistory, unfrozen.Snapshot);
        Assert.StartsWith("Unfroze book 'Dracula': book.md now says status: draft, so the book can be edited again. No checkpoint was made", unfrozen.Message, StringComparison.Ordinal);
        Assert.Equal(before, _den.Read(BookFile));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-12")]
    public void UnfreezingWritesStatusDraftRatherThanRemovingTheLine()
    {
        _den.Write(BookFile, _den.Read(BookFile).Replace("status: draft\n", "", StringComparison.Ordinal));
        var tools = new DenTools(_den.Root);

        tools.FreezeBook("dracula", null);
        Assert.Contains("\nstatus: final\n", _den.Read(BookFile), StringComparison.Ordinal);
        var unfrozen = tools.UnfreezeBook("dracula", null);

        Assert.Equal("draft", unfrozen.Status);
        Assert.Contains("\nstatus: draft\n", _den.Read(BookFile), StringComparison.Ordinal);

        // A book that isn't frozen is left as it is.
        var again = tools.UnfreezeBook("dracula", null);
        Assert.False(again.Changed);
        Assert.Equal(FreezeSnapshot.NotFrozen, again.Snapshot);
        Assert.Equal("Book 'Dracula' isn't frozen, so nothing changed.", again.Message);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void FreezingCheckpointsTheWholeDenAndTagsThatCheckpoint()
    {
        RunGit("init", "--quiet");
        var start = Commit("Start the Dracula notes");
        _den.Write(LucyFile, _den.Read(LucyFile) + "\nShe walks in her sleep.\n");
        var tools = WithGit();
        var status = tools.GetBookFreeze("dracula");
        Assert.True(status.History);
        Assert.Equal(Tag, status.NextTag);

        var frozen = tools.FreezeBook("dracula", status.Hash);

        Assert.Equal(FreezeSnapshot.Tagged, frozen.Snapshot);
        Assert.Equal(Tag, frozen.Tag);
        var head = RunGit("rev-parse", "HEAD").Trim();
        Assert.NotEqual(start, head);
        Assert.Equal(head, frozen.Checkpoint);
        Assert.Equal(head, RunGit("rev-parse", $"{Tag}^{{commit}}").Trim());
        Assert.Equal("Freeze book 'dracula' of universe 'dracula'", RunGit("log", "-1", "--format=%B").TrimEnd());
        Assert.Equal([BookFile, LucyFile], RunGit("show", "--name-only", "--format=", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Contains("\nstatus: final\n", RunGit("show", $"{Tag}:{BookFile}"), StringComparison.Ordinal);
        Assert.Equal($"Froze book 'Dracula': book.md now says status: final. Checkpointed the den ({head[..7]}). Tagged the snapshot '{Tag}'.", frozen.Message);

        // Freezing again: it's done, so nothing happens.
        var again = tools.FreezeBook("dracula", null);
        Assert.False(again.Changed);
        Assert.Equal(FreezeSnapshot.AlreadyTagged, again.Snapshot);
        Assert.Equal(Tag, again.Tag);
        Assert.Equal(head, RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(Tag + "\n", RunGit("tag", "--list"));
        var done = tools.GetBookFreeze("dracula");
        Assert.Equal("final", done.Status);
        Assert.False(done.Unfinished);
        Assert.Null(done.NextTag);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-12")]
    public void UnfreezingCheckpointsWithoutATagAndFreezingAgainAddsTheNextTag()
    {
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = WithGit();
        var first = tools.FreezeBook("dracula", null);

        var unfrozen = tools.UnfreezeBook("dracula", first.Hash);

        Assert.Equal(FreezeSnapshot.Checkpointed, unfrozen.Snapshot);
        var reopened = RunGit("rev-parse", "HEAD").Trim();
        Assert.Equal(reopened, unfrozen.Checkpoint);
        Assert.Equal("Unfreeze book 'dracula' of universe 'dracula'", RunGit("log", "-1", "--format=%B").TrimEnd());
        Assert.Equal(Tag + "\n", RunGit("tag", "--list"));
        Assert.Contains("\nstatus: draft\n", _den.Read(BookFile), StringComparison.Ordinal);
        Assert.Equal(Tag + "-2", tools.GetBookFreeze("dracula").NextTag);

        var refrozen = tools.FreezeBook("dracula", unfrozen.Hash);

        Assert.Equal(FreezeSnapshot.Tagged, refrozen.Snapshot);
        Assert.Equal(Tag + "-2", refrozen.Tag);
        Assert.Equal(first.Checkpoint, RunGit("rev-parse", $"{Tag}^{{commit}}").Trim());
        Assert.Equal(RunGit("rev-parse", "HEAD").Trim(), RunGit("rev-parse", $"{Tag}-2^{{commit}}").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-13")]
    public void ACheckpointGitRefusesLeavesTheBookFinalAndFreezingAgainResumes()
    {
        RunGit("init", "--quiet");
        var start = Commit("Start the Dracula notes");
        WriteHook("pre-commit", "#!/bin/sh\necho 'notes are locked' >&2\nexit 1\n");
        var tools = WithGit();

        var failed = tools.FreezeBook("dracula", null);

        Assert.True(failed.Changed);
        Assert.Equal("final", failed.Status);
        Assert.Equal(FreezeSnapshot.CheckpointFailed, failed.Snapshot);
        Assert.Null(failed.Checkpoint);
        Assert.Null(failed.Tag);
        Assert.Contains("the checkpoint failed, so nothing was committed or tagged", failed.Message, StringComparison.Ordinal);
        Assert.Contains("freeze the book again", failed.Message, StringComparison.Ordinal);
        Assert.Contains("\nstatus: final\n", _den.Read(BookFile), StringComparison.Ordinal);
        Assert.Equal(start, RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal("", RunGit("tag", "--list"));
        Assert.True(tools.GetBookFreeze("dracula").Unfinished);

        // The book is final, with its change not committed: freezing again checkpoints and tags.
        File.Delete(Path.Combine(_den.Root, ".git", "hooks", "pre-commit"));
        var resumed = tools.FreezeBook("dracula", null);

        Assert.False(resumed.Changed);
        Assert.Equal(FreezeSnapshot.Tagged, resumed.Snapshot);
        var head = RunGit("rev-parse", "HEAD").Trim();
        Assert.NotEqual(start, head);
        Assert.Equal(head, RunGit("rev-parse", $"{Tag}^{{commit}}").Trim());
        Assert.Contains("\nstatus: final\n", RunGit("show", $"{Tag}:{BookFile}"), StringComparison.Ordinal);
        Assert.StartsWith("Book 'Dracula' was already final. Checkpointed the den", resumed.Message, StringComparison.Ordinal);
        Assert.False(tools.GetBookFreeze("dracula").Unfinished);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-13")]
    public void ATagGitRefusesLeavesTheCheckpointAndFreezingAgainTagsIt()
    {
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");

        // A tag named like the freeze tag's folder: git can't make the freeze tag beside it.
        RunGit("tag", "book/dracula/dracula");
        var tools = WithGit();

        var failed = tools.FreezeBook("dracula", null);

        Assert.Equal(FreezeSnapshot.TagFailed, failed.Snapshot);
        var frozenAt = RunGit("rev-parse", "HEAD").Trim();
        Assert.Equal(frozenAt, failed.Checkpoint);
        Assert.Null(failed.Tag);
        Assert.Contains($"is final and checkpointed ({frozenAt[..7]}), but the tag failed", failed.Message, StringComparison.Ordinal);
        Assert.Contains("The checkpoint stands", failed.Message, StringComparison.Ordinal);
        Assert.True(tools.GetBookFreeze("dracula").Unfinished);

        // Later work is checkpointed meanwhile; the retry still tags the checkpoint that froze the book.
        RunGit("tag", "-d", "book/dracula/dracula");
        _den.Write("universes/dracula/entities/later.md", "---\ntype: note\n---\n# Later\n");
        Commit("Later notes");
        var resumed = tools.FreezeBook("dracula", null);

        Assert.Equal(FreezeSnapshot.Tagged, resumed.Snapshot);
        Assert.Equal(Tag, resumed.Tag);
        Assert.Equal(frozenAt, resumed.Checkpoint);
        Assert.Equal(frozenAt, RunGit("rev-parse", $"{Tag}^{{commit}}").Trim());
        Assert.Equal($"Book 'Dracula' was already final. Tagged its snapshot '{Tag}'.", resumed.Message);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void AFreezeBasedOnAStaleBookIsRefusedAndWritesNothing()
    {
        var tools = new DenTools(_den.Root);
        var status = tools.GetBookFreeze("dracula");
        _den.Write(BookFile, _den.Read(BookFile) + "\n");
        var now = _den.Read(BookFile);

        var stale = Assert.Throws<DenToolException>(() => tools.FreezeBook("dracula", status.Hash));
        Assert.Equal(ToolErrorCodes.StaleFile, stale.Code);
        Assert.Equal(now, _den.Read(BookFile));

        Assert.Equal(ToolErrorCodes.UnknownScope, Assert.Throws<DenToolException>(() => tools.FreezeBook("no-such-book", null)).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => tools.UnfreezeBook(" ", null)).Code);
    }

    public void Dispose()
    {
        var git = Path.Combine(_den.Root, ".git");
        if (Directory.Exists(git))
        {
            foreach (var file in Directory.EnumerateFiles(git, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        _den.Dispose();
        File.Delete(_config);
    }

    private void WriteHook(string name, string script)
    {
        var hook = Path.Combine(_den.Root, ".git", "hooks", name);
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, script, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string Commit(string message)
    {
        RunGit("add", "--all");
        RunGit("commit", "--quiet", "-m", message);
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _den.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in Git.Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Result : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error.Result}");
    }
}
