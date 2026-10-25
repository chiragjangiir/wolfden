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
