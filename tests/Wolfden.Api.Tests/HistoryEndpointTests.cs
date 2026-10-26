using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Wolfden.Git;
using Wolfden.Mcp;

namespace Wolfden.Api.Tests;

/// <summary>
/// The history endpoints (#27): an entity's checkpoints a page at a time,
/// and diffs of its file, from git, over a copy of the Dracula sample made a
/// repository. Git runs with the tests' own configuration, never the
/// developer's.
/// </summary>
public sealed class HistoryEndpointTests : IDisposable
{
    private const string MinaFile = "universes/dracula/books/dracula/entities/characters/mina-harker.md";

    private readonly TempDen _den = TempDen.Dracula();
    private readonly string _config;

    public HistoryEndpointTests()
    {
        _config = Path.Combine(Path.GetTempPath(), "wolfden-api-tests", $"gitconfig-{Guid.NewGuid():N}");
        File.WriteAllText(
            _config,
            "[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n",
            new UTF8Encoding(false));
    }

    private GitOptions Git => new()
    {
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = _config,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CEILING_DIRECTORIES"] = Path.Combine(Path.GetTempPath(), "wolfden-api-tests"),
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
        },
    };

    [Fact]
    [Trait("Scenario", "SC-WEB-9")]
    public async Task HistoryPagesThroughAnEntitysCheckpointsAsGetHistoryDoes()
    {
        var (first, second) = TwoCheckpoints();
        await using var server = await StartAsync();
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };

        var all = await server.GetJsonAsync("/api/history?entity=Mina%20Harker", HttpStatusCode.OK);
        SameAsMcp.Assert(tools.GetHistory("Mina Harker"), all);
        Assert.Equal("git", all.GetProperty("backend").GetString());
        Assert.Equal([second, first], all.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()));

        var page = await server.GetJsonAsync("/api/history?entity=Mina%20Harker&limit=1", HttpStatusCode.OK);
        Assert.True(page.GetProperty("truncated").GetBoolean());
        Assert.Equal(second, page.GetProperty("nextBefore").GetString());
        var next = await server.GetJsonAsync($"/api/history?entity=Mina%20Harker&limit=1&before={second}", HttpStatusCode.OK);
        SameAsMcp.Assert(tools.GetHistory("Mina Harker", limit: 1, before: second), next);
        Assert.Equal(first, Assert.Single(next.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());

        var unknown = await server.GetJsonAsync("/api/history?entity=Mina%20Harker&before=no-such-checkpoint", HttpStatusCode.NotFound);
        Assert.Equal("unknown_checkpoint", unknown.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-9")]
    public async Task ADiffIsOfOneCheckpointBetweenTwoOrSinceOneUpToNow()
    {
        var (first, second) = TwoCheckpoints();
        _den.Write(MinaFile, _den.Read(MinaFile) + "\nShe keeps a diary in shorthand.\n");
        await using var server = await StartAsync();

        // The first checkpoint is compared with nothing: the file is added.
        var own = await server.GetJsonAsync($"/api/history/diff?entity=Mina%20Harker&checkpoint={first}", HttpStatusCode.OK);
        Assert.True(own.GetProperty("available").GetBoolean());
        var diff = own.GetProperty("diff");
        Assert.Equal(JsonValueKind.Null, diff.GetProperty("from").ValueKind);
        Assert.Equal(first, diff.GetProperty("to").GetString());
        var file = Assert.Single(diff.GetProperty("files").EnumerateArray());
        Assert.Equal("added", file.GetProperty("change").GetProperty("kind").GetString());
        Assert.Equal(MinaFile, file.GetProperty("change").GetProperty("path").GetString());

        var changed = (await server.GetJsonAsync($"/api/history/diff?entity=Mina&checkpoint={second}", HttpStatusCode.OK)).GetProperty("diff");
        Assert.Equal(first, changed.GetProperty("from").GetString());
        var patch = Assert.Single(changed.GetProperty("files").EnumerateArray()).GetProperty("patch").GetString();
        Assert.Contains("+More about Mina.", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("shorthand", patch, StringComparison.Ordinal);

        var between = (await server.GetJsonAsync($"/api/history/diff?entity=Mina&from={first}&to={second}", HttpStatusCode.OK)).GetProperty("diff");
        Assert.Equal((first, second), (between.GetProperty("from").GetString(), between.GetProperty("to").GetString()));

        // With neither, since the latest checkpoint, up to the file as it is now, as get_history's diff.
        var now = await server.GetJsonAsync("/api/history/diff?entity=Mina", HttpStatusCode.OK);
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        SameAsMcp.Assert(tools.GetHistory("Mina", diff: true).Diff!, now.GetProperty("diff"));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("diff").GetProperty("to").ValueKind);
        Assert.Contains("+She keeps a diary in shorthand.", now.GetProperty("diff").GetProperty("files")[0].GetProperty("patch").GetString(), StringComparison.Ordinal);
        var since = (await server.GetJsonAsync($"/api/history/diff?entity=Mina&from={first}", HttpStatusCode.OK)).GetProperty("diff");
        Assert.Contains("shorthand", since.GetProperty("files")[0].GetProperty("patch").GetString(), StringComparison.Ordinal);

        Assert.Equal(
            "unknown_checkpoint",
            (await server.GetJsonAsync("/api/history/diff?entity=Mina&checkpoint=no-such-checkpoint", HttpStatusCode.NotFound)).GetProperty("code").GetString());
        Assert.Equal(
            "bad_argument",
            (await server.GetJsonAsync($"/api/history/diff?entity=Mina&checkpoint={first}&to={second}", HttpStatusCode.BadRequest)).GetProperty("code").GetString());

        // 'to' alone would compare backwards from the latest checkpoint.
        Assert.Equal("bad_argument", (await server.GetJsonAsync($"/api/history/diff?entity=Mina&to={first}", HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-10")]
    public async Task ARevisionThatCantBeOneIsABadArgumentAndALongOneIsCutShort()
    {
        TwoCheckpoints();
        await using var server = await StartAsync();

        foreach (var path in new[] { "/api/history?entity=Mina&before=-n1", "/api/history/diff?entity=Mina&checkpoint=a%20b", "/api/history/diff?entity=Mina&from=--all" })
        {
            Assert.Equal("bad_argument", (await server.GetJsonAsync(path, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }

        var long_ = new string('a', 300);
        foreach (var path in new[] { $"/api/history?entity=Mina&before={long_}", $"/api/history/diff?entity=Mina&checkpoint={long_}" })
        {
            var json = await server.GetJsonAsync(path, HttpStatusCode.NotFound);
            Assert.Equal("unknown_checkpoint", json.GetProperty("code").GetString());
            Assert.DoesNotContain(new string('a', 81), json.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-9")]
    public async Task ACheckpointThatRenamedTheNoteShowsTheRename()
    {
        TwoCheckpoints();
        var moved = "universes/dracula/books/dracula/entities/characters/mina-murray-harker.md";
        RunGit("mv", MinaFile, moved);
        var rename = Commit("Rename Mina's note");
        await using var server = await StartAsync();

        var file = Assert.Single((await server.GetJsonAsync($"/api/history/diff?entity=Mina&checkpoint={rename}", HttpStatusCode.OK))
            .GetProperty("diff").GetProperty("files").EnumerateArray());
        Assert.Equal("renamed", file.GetProperty("change").GetProperty("kind").GetString());
        Assert.Equal(moved, file.GetProperty("change").GetProperty("path").GetString());
        Assert.Equal(MinaFile, file.GetProperty("change").GetProperty("oldPath").GetString());

        // The list says so too, and a compare across the rename shows it rather than an added note (#181).
        var listed = Assert.Single(
            Assert.Single((await server.GetJsonAsync("/api/history?entity=Mina", HttpStatusCode.OK)).GetProperty("entries").EnumerateArray())
                .GetProperty("changes").EnumerateArray());
        Assert.Equal(("renamed", MinaFile), (listed.GetProperty("kind").GetString(), listed.GetProperty("oldPath").GetString()));
        var first = RunGit("rev-list", "--max-parents=0", "HEAD").Trim();
        foreach (var path in new[] { $"/api/history/diff?entity=Mina&from={first}&to={rename}", $"/api/history/diff?entity=Mina&from={first}" })
        {
            var compared = Assert.Single((await server.GetJsonAsync(path, HttpStatusCode.OK)).GetProperty("diff").GetProperty("files").EnumerateArray());
            Assert.Equal("renamed", compared.GetProperty("change").GetProperty("kind").GetString());
            Assert.Equal(MinaFile, compared.GetProperty("change").GetProperty("oldPath").GetString());
            Assert.Contains("+More about Mina.", compared.GetProperty("patch").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-11")]
    public async Task ANoteWithErrorsHasAHistoryByItsPath()
    {
        var (first, second) = TwoCheckpoints();
        _den.Write(MinaFile, _den.Read(MinaFile).Replace("name: Mina Harker", "name: [Mina Harker", StringComparison.Ordinal));
        await using var server = await StartAsync();
        var tools = new DenTools(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) };
        var path = Uri.EscapeDataString(MinaFile);

        // The den leaves the note out, so no name reaches it, and it has no page.
        Assert.Equal("unknown_entity", (await server.GetJsonAsync("/api/history?entity=Mina%20Harker", HttpStatusCode.NotFound)).GetProperty("code").GetString());
        Assert.Equal("unknown_entity", (await server.GetJsonAsync($"/api/entity?entity={path}", HttpStatusCode.NotFound)).GetProperty("code").GetString());

        // By its path, its history and what broke it.
        var history = await server.GetJsonAsync($"/api/history?entity={path}", HttpStatusCode.OK);
        SameAsMcp.Assert(tools.GetHistory(MinaFile), history);
        var entity = history.GetProperty("entity");
        Assert.Equal(
            (MinaFile, MinaFile, "mina-harker", "", "", "book", "dracula"),
            (entity.GetProperty("key").GetString(), entity.GetProperty("path").GetString(), entity.GetProperty("name").GetString(),
                entity.GetProperty("type").GetString(), entity.GetProperty("status").GetString(), entity.GetProperty("scopeKind").GetString(),
                entity.GetProperty("scopeKey").GetString()));
        Assert.Equal([second, first], history.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        var now = Assert.Single((await server.GetJsonAsync($"/api/history/diff?entity={path}", HttpStatusCode.OK)).GetProperty("diff").GetProperty("files").EnumerateArray());
        Assert.Contains("+name: [Mina Harker", now.GetProperty("patch").GetString(), StringComparison.Ordinal);

        // Only a note's file: not a container's, nor one that isn't there.
        foreach (var other in new[] { "universes/dracula/books/dracula/book.md", "universes/dracula/books/dracula/entities/characters/nobody.md" })
        {
            Assert.Equal(
                "unknown_entity",
                (await server.GetJsonAsync($"/api/history?entity={Uri.EscapeDataString(other)}", HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-9")]
    public async Task ADenWithoutHistorySaysSoRatherThanFailing()
    {
        await using var server = await Server.StartAsync(new WolfdenApiOptions(_den.Root));

        var history = await server.GetJsonAsync("/api/history?entity=Mina", HttpStatusCode.OK);
        Assert.Equal("none", history.GetProperty("backend").GetString());
        Assert.False(history.GetProperty("available").GetBoolean());
        Assert.Contains("no history", history.GetProperty("reason").GetString(), StringComparison.Ordinal);

        var diff = await server.GetJsonAsync("/api/history/diff?entity=Mina&checkpoint=abc123", HttpStatusCode.OK);
        Assert.False(diff.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, diff.GetProperty("diff").ValueKind);

        // A git repository with nothing checkpointed yet has no earlier version to compare with.
        RunGit("init", "--quiet");
        await using var git = await StartAsync();
        var empty = await git.GetJsonAsync("/api/history?entity=Mina", HttpStatusCode.OK);
        Assert.True(empty.GetProperty("available").GetBoolean());
        Assert.Equal(0, empty.GetProperty("count").GetInt32());
        var none = await git.GetJsonAsync("/api/history/diff?entity=Mina", HttpStatusCode.OK);
        Assert.False(none.GetProperty("available").GetBoolean());
        Assert.Contains("checkpoint", none.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _den.Dispose();
        try
        {
            File.Delete(_config);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private Task<Server> StartAsync() =>
        Server.StartAsync(new WolfdenApiOptions(_den.Root) { History = () => GitHistory.ForDen(_den.Root, Git) });

    private (string First, string Second) TwoCheckpoints()
    {
        RunGit("init", "--quiet");
        var first = Commit("Start the Dracula notes");
        _den.Write(MinaFile, _den.Read(MinaFile) + "\nMore about Mina.\n");
        var second = Commit("A note about Mina");
        return (first, second);
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
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error.Result}");
        return output.Result;
    }
}
