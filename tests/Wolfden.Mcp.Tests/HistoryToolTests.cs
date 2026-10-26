using System.Diagnostics;
using System.Text;
using Wolfden.Core.History;
using Wolfden.Git;

namespace Wolfden.Mcp.Tests;

/// <summary>
/// <c>get_history</c> (#19): an entity's checkpoints and what changed
/// since, from git, over a copy of the Dracula sample made a repository.
/// Git runs with the tests' own configuration, never the developer's.
/// </summary>
public sealed class HistoryToolTests : IDisposable
{
    private const string LucyFile = "universes/dracula/books/dracula/entities/characters/lucy-westenra.md";

    private readonly TempDen _den = TempDen.Dracula();
    private readonly string _config;

    public HistoryToolTests()
    {
        _config = Path.Combine(Path.GetTempPath(), "wolfden-mcp-tests", $"gitconfig-{Guid.NewGuid():N}");
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

    [Fact]
    [Trait("Scenario", "SC-MCP-14")]
    public void TheAssistantSeesAnEntitysCheckpointsAPageAtATimeAndWhatChangedSince()
    {
        RunGit("init", "--quiet");
        var first = Commit("Start the Dracula notes");
        _den.Write("universes/dracula/books/dracula/entities/characters/mina-harker.md", _den.Read("universes/dracula/books/dracula/entities/characters/mina-harker.md") + "\nMore about Mina.\n");
        Commit("A note about Mina");
        _den.Write(LucyFile, _den.Read(LucyFile) + "\nShe walks in her sleep.\n");
        var second = Commit("Lucy sleepwalks");
        _den.Write(LucyFile, _den.Read(LucyFile) + "\nNot checkpointed yet.\n");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };

        var all = tools.GetHistory("Lucy Westenra", diff: true);

        Assert.True(all.Available);
        Assert.Equal("git", all.Backend);
        Assert.Equal(LucyFile, all.Entity.Path);
        Assert.Equal([second, first], all.Entries!.Select(e => e.Id));
        Assert.Equal("Lucy sleepwalks", all.Entries![0].Message);
        Assert.Equal([new HistoryChangeView("modified", LucyFile, null)], all.Entries[0].Changes);
        Assert.False(all.Truncated);
        Assert.Null(all.NextBefore);
        var patch = Assert.Single(all.Diff!.Files);
        Assert.Equal(second, all.Diff.From);
        Assert.Contains("\n+Not checkpointed yet.\n", patch.Patch, StringComparison.Ordinal);
        Assert.DoesNotContain("\n+She walks in her sleep.\n", patch.Patch, StringComparison.Ordinal);

        // A page at a time.
        var page = tools.GetHistory("Lucy", limit: 1);
        Assert.Equal([second], page.Entries!.Select(e => e.Id));
        Assert.True(page.Truncated);
        Assert.Equal(second, page.NextBefore);
        var next = tools.GetHistory("Lucy", limit: 1, before: page.NextBefore);
        Assert.Equal([first], next.Entries!.Select(e => e.Id));
        Assert.False(next.Truncated);
        Assert.Null(next.Diff);

        // Since a chosen checkpoint: both later edits.
        var since = tools.GetHistory("Lucy", diffFrom: first);
        Assert.Equal(first, since.Diff!.From);
        var sincePatch = Assert.Single(since.Diff.Files).Patch;
        Assert.Contains("\n+She walks in her sleep.\n", sincePatch, StringComparison.Ordinal);
        Assert.Contains("\n+Not checkpointed yet.\n", sincePatch, StringComparison.Ordinal);

        var bad = Assert.Throws<DenToolException>(() => tools.GetHistory("Lucy", before: "not-a-checkpoint"));
        Assert.Equal(ToolErrorCodes.UnknownCheckpoint, bad.Code);
        Assert.Equal(ToolErrorCodes.UnknownCheckpoint, Assert.Throws<DenToolException>(() => tools.GetHistory("Lucy", diffFrom: "--output=x")).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-14")]
    public void WithoutHistoryTheAnswerSaysSoAndIsntAnError()
    {
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };

        var result = tools.GetHistory("Lucy", diff: true);

        Assert.False(result.Available);
        Assert.Equal("none", result.Backend);
        Assert.StartsWith("This den has no history, so a note's history isn't available.", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Entries);
        Assert.Equal(LucyFile, result.Entity.Path);
        Assert.Equal(ToolErrorCodes.UnknownEntity, Assert.Throws<DenToolException>(() => tools.GetHistory("Nobody At All")).Code);
        Assert.Equal(ToolErrorCodes.BadArgument, Assert.Throws<DenToolException>(() => tools.GetHistory("Lucy", limit: 0)).Code);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    [Trait("Scenario", "SC-HIST-4")]
    public void WritesWaitForACheckpointThatNamesTheAgentAsCoAuthor()
    {
        RunGit("init", "--quiet");
        var start = Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };

        var made = tools.CreateEntity("location", "Whitby Abbey");
        var lucy = tools.GetEntity("Lucy Westenra").Entity!;
        tools.AppendSection("Lucy Westenra", "Overview", "She loves the view from the East Cliff.", lucy.Hash!);

        // Writes aren't commits.
        Assert.Equal(start, RunGit("rev-parse", "HEAD").Trim());

        var result = tools.Checkpoint("Whitby notes", "Claude <noreply@anthropic.com>");

        Assert.True(result.Available);
        Assert.True(result.Recorded);
        Assert.EndsWith("Co-Authored-By: Claude <noreply@anthropic.com>", result.Checkpoint!.Message.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal([LucyFile, made.Path], result.Checkpoint.Changes.Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.Contains("Co-Authored-By: Claude <noreply@anthropic.com>", RunGit("log", "-1", "--format=%B"), StringComparison.Ordinal);

        // Nothing new: nothing recorded.
        var again = tools.Checkpoint("Again", "Claude <noreply@anthropic.com>");
        Assert.True(again.Available);
        Assert.False(again.Recorded);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    public void ACheckpointOfSomePathsRecordsOnlyThem()
    {
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        var abbey = tools.CreateEntity("location", "Whitby Abbey");
        var lucy = tools.GetEntity("Lucy Westenra").Entity!;
        tools.AppendSection("Lucy Westenra", "Overview", "She loves the view from the East Cliff.", lucy.Hash!);

        var result = tools.Checkpoint("Lucy only", "Claude <noreply@anthropic.com>", [LucyFile]);

        Assert.Equal([LucyFile], result.Checkpoint!.Changes.Select(c => c.Path));
        Assert.Equal([LucyFile], result.WrittenBySession);
        Assert.Empty(result.ChangedOutsideSession!);
        Assert.Contains(abbey.Path, RunGit("status", "--porcelain", "--untracked-files=all"), StringComparison.Ordinal);

        // The abbey wasn't recorded, so it's still the session's next time.
        var rest = tools.Checkpoint("The abbey", "Claude <noreply@anthropic.com>");
        Assert.Equal([abbey.Path], rest.WrittenBySession);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    public void ACheckpointSaysWhichFilesTheSessionWroteAndWhichChangedOutsideIt()
    {
        const string MinaFile = "universes/dracula/books/dracula/entities/characters/mina-harker.md";
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        var abbey = tools.CreateEntity("location", "Whitby Abbey");
        var lucy = tools.GetEntity("Lucy Westenra").Entity!;
        tools.AppendSection("Lucy Westenra", "Overview", "She loves the view from the East Cliff.", lucy.Hash!);
        _den.Write(MinaFile, _den.Read(MinaFile) + "\nFixed a typo in the editor.\n");

        var result = tools.Checkpoint("Whitby notes", "Claude <noreply@anthropic.com>");

        Assert.Equal([LucyFile, abbey.Path], result.WrittenBySession);
        Assert.Equal([MinaFile], result.ChangedOutsideSession);

        // Afresh after a checkpoint: only what's written since is the session's.
        _den.Write(LucyFile, _den.Read(LucyFile) + "\nAnd an editor edit.\n");
        var next = tools.Checkpoint("Lucy by hand", "Claude <noreply@anthropic.com>");
        Assert.Empty(next.WrittenBySession!);
        Assert.Equal([LucyFile], next.ChangedOutsideSession);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    public void ASessionWriteTheWriterCommittedElsewhereIsntTheSessionsAnyMore()
    {
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        var lucy = tools.GetEntity("Lucy Westenra").Entity!;
        tools.AppendSection("Lucy Westenra", "Overview", "She loves the view from the East Cliff.", lucy.Hash!);

        // The writer commits in their own git client, then edits Lucy by hand.
        Commit("Committed by hand");
        _den.Write(LucyFile, _den.Read(LucyFile) + "\nA hand edit.\n");

        var result = tools.Checkpoint("After the hand edit", "Claude <noreply@anthropic.com>");

        Assert.Empty(result.WrittenBySession!);
        Assert.Equal([LucyFile], result.ChangedOutsideSession);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    public void AFileEditedByTheSessionAndTheWriterIsInBothLists()
    {
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        var lucy = tools.GetEntity("Lucy Westenra").Entity!;
        tools.AppendSection("Lucy Westenra", "Overview", "She loves the view from the East Cliff.", lucy.Hash!);
        _den.Write(LucyFile, _den.Read(LucyFile) + "\nThe writer's line too.\n");

        var result = tools.Checkpoint("Both", "Claude <noreply@anthropic.com>");

        Assert.Equal([LucyFile], result.WrittenBySession);
        Assert.Equal([LucyFile], result.ChangedOutsideSession);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-22")]
    public void AFileAFailedWritePutBackIsntTheSessions()
    {
        const string Swales = "universes/dracula/books/dracula/entities/characters/mr-swales.md";
        const string Note = "universes/dracula/entities/notes/whitby-dialect.md";
        _den.Write(Note, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Whitby dialect\nstatus: draft\n---\n\nSee [[mr-swales#Voice]].\n");
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var hash = new DenTools(_den.Root).GetEntity("Mr. Swales").Entity!.Hash!;
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git), Store = new WriteToolTests.FailingStore(_den.Root, Note) };
        Assert.Throws<DenToolException>(() => tools.RenameSection("Mr. Swales", "Voice", "Dialect", hash));

        // Swales was written, then put back; the writer then edits it by hand.
        _den.Write(Swales, _den.Read(Swales) + "\nA hand edit.\n");
        var result = tools.Checkpoint("Hand edit", "Claude <noreply@anthropic.com>");

        Assert.Empty(result.WrittenBySession!);
        Assert.Equal([Swales], result.ChangedOutsideSession);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    [Trait("Scenario", "SC-MCP-22")]
    public void AMovedFilesOldPathIsTheSessionsWhenGitSeesADeleteAndAnAdd()
    {
        // A note so short that the id and 'from' the move adds leave git no rename to see.
        const string Old = "universes/dracula/books/dracula/entities/notes/q.md";
        const string New = "universes/dracula/entities/notes/q.md";
        _den.Write(Old, "---\ntype: note\nname: Q\n---\n");
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        tools.MoveEntity("Q", tools.GetEntity("Q").Entity!.Hash!);

        var result = tools.Checkpoint("Move Q", "Claude <noreply@anthropic.com>");

        Assert.Equal("deleted", result.Checkpoint!.Changes.Single(c => c.Path == Old).Kind);
        Assert.Equal([Old, New], result.WrittenBySession!.Order(StringComparer.Ordinal));
        Assert.Empty(result.ChangedOutsideSession!);
        Assert.Empty(tools.SessionDeletions);
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    [Trait("Scenario", "SC-MCP-22")]
    public void AMoveTheWriterCommittedElsewhereIsForgottenAtTheNextCheckpoint()
    {
        const string Old = "universes/dracula/books/dracula/entities/notes/q.md";
        const string New = "universes/dracula/entities/notes/q.md";
        _den.Write(Old, "---\ntype: note\nname: Q\n---\n");
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        tools.MoveEntity("Q", tools.GetEntity("Q").Entity!.Hash!);
        Assert.Equal([Old], tools.SessionDeletions);

        // A checkpoint of other paths leaves the deletion to record.
        _den.Write("universes/dracula/entities/notes/elsewhere.md", "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z3\ntype: note\nname: Elsewhere\nstatus: draft\n---\n");
        tools.Checkpoint("Elsewhere", "Claude <noreply@anthropic.com>", ["universes/dracula/entities/notes/elsewhere.md"]);
        Assert.Equal([Old], tools.SessionDeletions);

        // The writer commits the move by hand; the next checkpoint records nothing, and forgets it.
        Commit("The writer commits the move");
        var result = tools.Checkpoint("Nothing new", "Claude <noreply@anthropic.com>");

        Assert.False(result.Recorded);
        Assert.Empty(tools.SessionDeletions);
        Assert.False(File.Exists(Path.Combine(_den.Root, Old)));
        Assert.True(File.Exists(Path.Combine(_den.Root, New)));
    }

    [Fact]
    [Trait("Scenario", "SC-MCP-29")]
    [Trait("Scenario", "SC-MCP-22")]
    public void AFileTheSessionEditedThenMovedIsntChangedOutsideIt()
    {
        const string Old = "universes/dracula/books/dracula/entities/notes/q.md";
        const string New = "universes/dracula/entities/notes/q.md";
        _den.Write(Old, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z1\ntype: note\nname: Q\nstatus: draft\nfrom: dracula\n---\n");
        RunGit("init", "--quiet");
        Commit("Start the Dracula notes");
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };

        // The session's edit changes most of the file, so git sees no rename either.
        var edited = tools.AppendSection("Q", "Overview", string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"A long paragraph about Q, number {i}, so the file is mostly new.")), tools.GetEntity("Q").Entity!.Hash!);
        tools.MoveEntity("Q", edited.Hash);

        var result = tools.Checkpoint("Edit and move Q", "Claude <noreply@anthropic.com>");

        Assert.Equal("deleted", result.Checkpoint!.Changes.Single(c => c.Path == Old).Kind);
        Assert.Equal([Old, New], result.WrittenBySession!.Order(StringComparer.Ordinal));
        Assert.Empty(result.ChangedOutsideSession!);

        // A file put back at the old path by hand, later, is the writer's.
        _den.Write(Old, "---\nid: 01M4TF5F0EKGGWD5FNVBAEY8Z2\ntype: note\nname: Q again\nstatus: draft\n---\n");
        var next = tools.Checkpoint("Q again", "Claude <noreply@anthropic.com>");
        Assert.Empty(next.WrittenBySession!);
        Assert.Equal([Old], next.ChangedOutsideSession);
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
