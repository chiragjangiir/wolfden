using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Wolfden.Git;

namespace Wolfden.Api.Tests;

/// <summary>
/// Freezing and unfreezing a book from the web UI (#31): <c>GET
/// /api/book/freeze</c> and <c>POST /api/book/freeze</c> and
/// <c>/api/book/unfreeze</c>, over a copy of the Dracula sample, with and
/// without git. Git runs with the tests' own configuration, never the
/// developer's.
/// </summary>
public sealed class FreezeEndpointTests : IDisposable
{
    private const string BookFile = "universes/dracula/books/dracula/book.md";
    private const string LucyFile = "universes/dracula/books/dracula/entities/characters/lucy-westenra.md";
    private const string Tag = "book/dracula/dracula/final";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<TempDen> _dens = [];
    private readonly string _config;

    public FreezeEndpointTests()
    {
        _config = Path.Combine(Path.GetTempPath(), "wolfden-api-tests", $"gitconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(_config)!);
        File.WriteAllText(
            _config,
            "[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[tag]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n",
            new UTF8Encoding(false));
    }

    public void Dispose()
    {
        foreach (var den in _dens)
        {
            den.Dispose();
        }

        File.Delete(_config);
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
    [Trait("Scenario", "SC-WEB-66")]
    public async Task FreezingCheckpointsAndTagsAndUnfreezingCheckpointsAndTheBooksNotesFollow()
    {
        var den = Den(TempDen.Dracula());
        RunGit(den, "init", "--quiet");
        Commit(den, "Start the Dracula notes");
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { History = () => GitHistory.ForDen(den.Root, Git) });

        var status = await server.GetJsonAsync("/api/book/freeze?book=dracula", HttpStatusCode.OK);
        Assert.Equal("draft", status.GetProperty("status").GetString());
        Assert.Equal("Dracula", status.GetProperty("name").GetString());
        Assert.Equal(BookFile, status.GetProperty("path").GetString());
        Assert.True(status.GetProperty("history").GetBoolean());
        Assert.Equal(Tag, status.GetProperty("nextTag").GetString());

        var (code, frozen) = await PostAsync(server, "/api/book/freeze", new { book = "dracula", expectedHash = status.GetProperty("hash").GetString() });

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("final", frozen.GetProperty("status").GetString());
        Assert.Equal("tagged", frozen.GetProperty("snapshot").GetString());
        Assert.Equal(Tag, frozen.GetProperty("tag").GetString());
        Assert.Equal(RunGit(den, "rev-parse", "HEAD").Trim(), frozen.GetProperty("checkpoint").GetString());
        Assert.Contains("Tagged the snapshot", frozen.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The book's notes can't be edited now, and their pages say why.
        var source = await server.GetJsonAsync("/api/entity/source?entity=Lucy%20Westenra", HttpStatusCode.OK);
        Assert.Contains("final (frozen)", source.GetProperty("frozen").GetString(), StringComparison.Ordinal);
        var hierarchy = await server.GetJsonAsync("/api/hierarchy", HttpStatusCode.OK);
        Assert.Equal("final", hierarchy.GetProperty("universe").GetProperty("books")[0].GetProperty("status").GetString());

        var (unfreezeCode, unfrozen) = await PostAsync(server, "/api/book/unfreeze", new { book = "dracula", universe = "dracula", expectedHash = frozen.GetProperty("hash").GetString() });

        Assert.Equal(HttpStatusCode.OK, unfreezeCode);
        Assert.Equal("draft", unfrozen.GetProperty("status").GetString());
        Assert.Equal("checkpointed", unfrozen.GetProperty("snapshot").GetString());
        Assert.Equal(JsonValueKind.Null, unfrozen.GetProperty("tag").ValueKind);
        Assert.Equal("Unfreeze book 'dracula' of universe 'dracula'", RunGit(den, "log", "-1", "--format=%s").Trim());
        Assert.Contains("\nstatus: draft\n", den.Read(BookFile), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await server.GetJsonAsync("/api/entity/source?entity=Lucy%20Westenra", HttpStatusCode.OK)).GetProperty("frozen").ValueKind);
        Assert.Equal(Tag + "-2", (await server.GetJsonAsync("/api/book/freeze?book=dracula", HttpStatusCode.OK)).GetProperty("nextTag").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-66")]
    public async Task WithoutHistoryFreezingSaysNoSnapshotWasTaken()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var status = await server.GetJsonAsync("/api/book/freeze?book=dracula", HttpStatusCode.OK);
        Assert.False(status.GetProperty("history").GetBoolean());
        Assert.Contains("no history", status.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("nextTag").ValueKind);

        var (code, frozen) = await PostAsync(server, "/api/book/freeze", new { book = "dracula", expectedHash = status.GetProperty("hash").GetString() });

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("no_history", frozen.GetProperty("snapshot").GetString());
        Assert.Contains("No snapshot was taken", frozen.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("\nstatus: final\n", den.Read(BookFile), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-66")]
    public async Task AStaleOrMalformedFreezeIsRefusedAndWritesNothing()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var hash = (await server.GetJsonAsync("/api/book/freeze?book=dracula", HttpStatusCode.OK)).GetProperty("hash").GetString();
        den.Write(BookFile, den.Read(BookFile) + "\n");
        var before = den.Read(BookFile);

        var (stale, staleJson) = await PostAsync(server, "/api/book/freeze", new { book = "dracula", expectedHash = hash });
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Equal("stale_file", staleJson.GetProperty("code").GetString());

        var (noHash, noHashJson) = await PostAsync(server, "/api/book/freeze", new { book = "dracula" });
        Assert.Equal(HttpStatusCode.BadRequest, noHash);
        Assert.Contains("expectedHash", noHashJson.GetProperty("message").GetString(), StringComparison.Ordinal);

        var (extra, extraJson) = await PostAsync(server, "/api/book/unfreeze", new { book = "dracula", expectedHash = hash, status = "draft" });
        Assert.Equal(HttpStatusCode.BadRequest, extra);
        Assert.Equal("bad_argument", extraJson.GetProperty("code").GetString());

        var (unknown, _) = await PostAsync(server, "/api/book/freeze", new { book = "no-such-book", expectedHash = hash });
        Assert.Equal(HttpStatusCode.NotFound, unknown);

        var (form, _) = await PostAsync(server, "/api/book/freeze", "book=dracula", "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form);

        using var get = await server.Client.GetAsync("/api/book/unfreeze", Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal("POST", get.Content.Headers.Allow.Single());

        Assert.Equal(before, den.Read(BookFile));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-66")]
    public async Task APinnedServerWontShowOrFreezeAnotherUniversesBook()
    {
        var den = Den(TempDen.Starfall());
        const string Tide = "universes/drift/books/tide/book.md";
        var before = den.Read(Tide);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root) { PinnedUniverse = "starfall" });

        using var get = await server.Client.GetAsync("/api/book/freeze?book=tide&universe=drift", Token);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        var (bare, _) = await PostAsync(server, "/api/book/freeze", new { book = "tide", expectedHash = "0" });
        Assert.Equal(HttpStatusCode.NotFound, bare);
        var (named, _) = await PostAsync(server, "/api/book/freeze", new { book = "tide", universe = "drift", expectedHash = "0" });
        Assert.Equal(HttpStatusCode.BadRequest, named);

        Assert.Equal(before, den.Read(Tide));
        Assert.Equal("ash", (await server.GetJsonAsync("/api/book/freeze?book=ash", HttpStatusCode.OK)).GetProperty("book").GetString());
    }

    // ------------------------------------------------------------ helpers

    private TempDen Den(TempDen den)
    {
        _dens.Add(den);
        return den;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(Server server, string path, object body, string contentType = "application/json")
    {
        using var content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, contentType);
        using var response = await server.Client.PostAsync(path, content, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private void Commit(TempDen den, string message)
    {
        RunGit(den, "add", "--all");
        RunGit(den, "commit", "--quiet", "-m", message);
    }

    private string RunGit(TempDen den, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = den.Root,
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
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error.Result}");
        return output.Result;
    }
}
