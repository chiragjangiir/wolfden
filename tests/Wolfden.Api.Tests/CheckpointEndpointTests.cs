using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Wolfden.Core.History;
using Wolfden.Git;

namespace Wolfden.Api.Tests;

/// <summary>
/// Checkpoints from the web UI (#30): <c>GET /api/checkpoint</c> says what a
/// checkpoint now would record, and <c>POST /api/checkpoint</c> makes one,
/// through git, over a copy of a sample den. Git runs with the tests' own
/// configuration, never the developer's.
/// </summary>
public sealed class CheckpointEndpointTests : IDisposable
{
    private const string MinaFile = "universes/dracula/books/dracula/entities/characters/mina-harker.md";
    private const string LucyFile = "universes/dracula/books/dracula/entities/characters/lucy-westenra.md";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<TempDen> _dens = [];
    private readonly string _config;

    public CheckpointEndpointTests()
    {
        _config = Path.Combine(Path.GetTempPath(), "wolfden-api-tests", $"gitconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(_config)!);
        File.WriteAllText(
            _config,
            "[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n",
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
    [Trait("Scenario", "SC-WEB-51")]
    public async Task WithoutHistoryTheStatusSaysHowToTurnItOnAndACheckpointIsRefused()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.Equal("none", status.GetProperty("backend").GetString());
        Assert.Contains("no history", status.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var enable = status.GetProperty("enable").GetString()!;
        Assert.Contains("git init", enable, StringComparison.Ordinal);
        Assert.Contains("wolfden init --git", enable, StringComparison.Ordinal);
        Assert.Equal(0, status.GetProperty("count").GetInt32());

        var (code, made) = await PostAsync(server, """{"message": "Notes pass after drafting ch 12"}""");
        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.Equal("history_unavailable", made.GetProperty("code").GetString());
        Assert.Contains("still saved in the files", made.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    [Trait("Scenario", "SC-WEB-52")]
    public async Task TheStatusListsWhatChangedAndACheckpointRecordsItWithTheWritersMessage()
    {
        var den = Repository(TempDen.Dracula());
        var first = Commit(den, "Start the Dracula notes");
        await using var server = await StartAsync(den);

        var clean = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.True(clean.GetProperty("available").GetBoolean());
        Assert.Equal(0, clean.GetProperty("count").GetInt32());
        Assert.Equal(first, clean.GetProperty("latest").GetProperty("id").GetString());
        Assert.Equal(["id", "when"], clean.GetProperty("latest").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, clean.GetProperty("inProgress").ValueKind);

        // A notes pass after drafting a chapter: two notes edited, one added.
        den.Write(MinaFile, den.Read(MinaFile) + "\nShe keeps a diary in shorthand.\n");
        den.Write(LucyFile, den.Read(LucyFile) + "\nThree proposals in one day.\n");
        den.Write("universes/dracula/entities/locations/golden-krone.md", "---\ntype: location\nname: The Golden Krone\n---\n\n# The Golden Krone\n");

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(3, status.GetProperty("count").GetInt32());
        Assert.Equal(
            [("modified", LucyFile), ("modified", MinaFile), ("added", "universes/dracula/entities/locations/golden-krone.md")],
            status.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("kind").GetString(), c.GetProperty("path").GetString())));
        Assert.All(status.GetProperty("changes").EnumerateArray(), c => Assert.Equal("dracula", c.GetProperty("universe").GetString()));
        Assert.All(status.GetProperty("changes").EnumerateArray(), c => Assert.False(c.GetProperty("savedHere").GetBoolean()));

        var (code, made) = await PostAsync(server, """{"message": "  Notes pass after drafting ch 12  "}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(made.GetProperty("recorded").GetBoolean());
        var checkpoint = made.GetProperty("checkpoint");
        Assert.Equal("Notes pass after drafting ch 12", checkpoint.GetProperty("message").GetString());
        Assert.Equal("Test Writer", checkpoint.GetProperty("author").GetString());
        Assert.Equal(3, checkpoint.GetProperty("files").GetInt32());
        Assert.Equal(3, made.GetProperty("changes").GetArrayLength());

        // No co-author: the writer made it. Only the den's files were committed.
        var id = checkpoint.GetProperty("id").GetString()!;
        Assert.Equal("Notes pass after drafting ch 12", RunGit(den, "log", "-1", "--format=%B", id).TrimEnd('\n'));

        // The indicator goes quiet, and the note's History tab lists the new checkpoint.
        var after = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(0, after.GetProperty("count").GetInt32());
        Assert.Equal(id, after.GetProperty("latest").GetProperty("id").GetString());
        var history = await server.GetJsonAsync("/api/history?entity=Mina%20Harker", HttpStatusCode.OK);
        Assert.Equal(id, history.GetProperty("entries")[0].GetProperty("id").GetString());

        // A second checkpoint straight after has nothing to record.
        var (againCode, again) = await PostAsync(server, """{"message": "Again"}""");
        Assert.Equal(HttpStatusCode.OK, againCode);
        Assert.False(again.GetProperty("recorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("checkpoint").ValueKind);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    public async Task BeforeTheFirstCheckpointEveryDenFileIsListed()
    {
        var den = Repository(TempDen.Dracula());
        await using var server = await StartAsync(den);

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("latest").ValueKind);
        var files = Directory.EnumerateFiles(den.Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(den.Root, f).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(n => n.StartsWith('.')))
            .Count();
        Assert.Equal(files, status.GetProperty("count").GetInt32());
        Assert.All(status.GetProperty("changes").EnumerateArray(), c => Assert.Equal("added", c.GetProperty("kind").GetString()));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    public async Task ASaveThroughThisServerIsToldApartFromAChangeMadeElsewhere()
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        var options = new WolfdenApiOptions(den.Root) { History = () => GitHistory.ForDen(den.Root, Git) };
        await using var server = await Server.StartAsync(options);

        // The web UI's save records what it wrote (#28's write endpoints call this).
        var saved = Encoding.UTF8.GetBytes(den.Read(MinaFile) + "\nSaved in the web UI.\n");
        File.WriteAllBytes(Path.Combine(den.Root, MinaFile), saved);
        options.Ledger.Wrote(MinaFile, saved);
        den.Write(LucyFile, den.Read(LucyFile) + "\nEdited in another editor.\n");

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(
            [(LucyFile, false), (MinaFile, true)],
            status.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("path").GetString(), c.GetProperty("savedHere").GetBoolean())));

        // Changed again elsewhere after the save: no longer only the server's.
        den.Write(MinaFile, den.Read(MinaFile) + "\nAnd then by hand.\n");
        status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.All(status.GetProperty("changes").EnumerateArray(), c => Assert.False(c.GetProperty("savedHere").GetBoolean()));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    public async Task AWebSaveShowsAsSavedInWolfdenAndStartsAfreshAfterTheCheckpoint()
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        den.Write(LucyFile, den.Read(LucyFile) + "\nEdited in another editor.\n");
        await using var server = await StartAsync(den);

        // A save through the web UI's write endpoints (#28).
        var source = await server.GetJsonAsync("/api/entity/source?entity=Mina%20Harker", HttpStatusCode.OK);
        using (var response = await server.Client.PostAsync(
            "/api/edit/lead",
            new StringContent(
                JsonSerializer.Serialize(new { entity = "Mina Harker", expectedHash = source.GetProperty("hash").GetString(), text = "# Mina Harker\n\nShe keeps a diary in shorthand." }),
                Encoding.UTF8,
                "application/json"),
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(
            [(LucyFile, false), (MinaFile, true)],
            status.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("path").GetString(), c.GetProperty("savedHere").GetBoolean())));

        var (code, made) = await PostAsync(server, """{"message": "Notes pass after drafting ch 12"}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(
            [(LucyFile, false), (MinaFile, true)],
            made.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("path").GetString(), c.GetProperty("savedHere").GetBoolean())));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-60")]
    public async Task AWebFieldSaveShowsAsSavedInWolfden()
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        await using var server = await StartAsync(den);

        // A field save through /api/edit/field (#28, part b) goes through the same DenTools, so the ledger records it.
        var source = await server.GetJsonAsync("/api/entity/source?entity=Mina%20Harker", HttpStatusCode.OK);
        using (var response = await server.Client.PostAsync(
            "/api/edit/field",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    entity = "Mina Harker", expectedHash = source.GetProperty("hash").GetString(), field = "placeholder", changes = new[] { new { op = "set", value = true } },
                }),
                Encoding.UTF8,
                "application/json"),
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(
            [(MinaFile, true)],
            status.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("path").GetString(), c.GetProperty("savedHere").GetBoolean())));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-53")]
    public async Task ASaveDoesntWaitForACheckpointThatsWaitingOnGit()
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        den.Write(LucyFile, den.Read(LucyFile) + "\nMore.\n");

        // A pre-commit hook that waits, as git waits on a signing passphrase prompt.
        var gate = Path.Combine(den.Root, ".git", "release");
        var hook = Path.Combine(den.Root, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\nwhile [ ! -f .git/release ]; do sleep 0.1; done\nexit 0\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await using var server = await StartAsync(den);
        var source = await server.GetJsonAsync("/api/entity/source?entity=Mina%20Harker", HttpStatusCode.OK);
        var checkpoint = PostAsync(server, """{"message": "Planning session: harbour subplot"}""");
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        Assert.False(checkpoint.IsCompleted);

        // A save, and the status, while git waits.
        var save = server.Client.PostAsync(
            "/api/edit/lead",
            new StringContent(
                JsonSerializer.Serialize(new { entity = "Mina Harker", expectedHash = source.GetProperty("hash").GetString(), text = "# Mina Harker\n\nSaved meanwhile." }),
                Encoding.UTF8,
                "application/json"),
            Token);
        using (var saved = await save.WaitAsync(TimeSpan.FromSeconds(20), Token))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        Assert.False(checkpoint.IsCompleted);
        File.WriteAllText(gate, "");
        var (code, _) = await checkpoint;
        Assert.Equal(HttpStatusCode.OK, code);

        // Saved during the checkpoint, so it may not be in it: still marked as the server's.
        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        var mina = status.GetProperty("changes").EnumerateArray().SingleOrDefault(c => c.GetProperty("path").GetString() == MinaFile);
        if (mina.ValueKind != JsonValueKind.Undefined)
        {
            Assert.True(mina.GetProperty("savedHere").GetBoolean());
        }
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-50")]
    [InlineData("""{"message": ""}""", "bad_argument")]
    [InlineData("""{"message": "   "}""", "bad_argument")]
    [InlineData("""{}""", "bad_argument")]
    [InlineData("""{"message": 12}""", "bad_argument")]
    [InlineData("""{"message": "ok", "coAuthor": "Someone <a@example.com>"}""", "bad_argument")]
    [InlineData("""["Notes pass"]""", "bad_argument")]
    [InlineData("""{"message": "bell\u0007"}""", "bad_argument")]
    [InlineData("not json", "bad_argument")]
    public async Task ACheckpointNeedsAMessage(string body, string code)
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        den.Write(MinaFile, den.Read(MinaFile) + "\nMore.\n");
        await using var server = await StartAsync(den);

        var (status, json) = await PostAsync(server, body);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal(1, (await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK)).GetProperty("count").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-50")]
    public async Task ATooLongMessageIsRefused()
    {
        var den = Repository(TempDen.Dracula());
        await using var server = await StartAsync(den);
        var (status, json) = await PostAsync(server, JsonSerializer.Serialize(new { message = new string('a', CheckpointEndpoints.LongestMessage + 1) }));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_argument", json.GetProperty("code").GetString());
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-3")]
    [InlineData("Origin", "https://attacker.example", "application/json", HttpStatusCode.Forbidden, "cross_site")]
    [InlineData("Sec-Fetch-Site", "cross-site", "application/json", HttpStatusCode.Forbidden, "cross_site")]
    [InlineData(null, null, "text/plain", HttpStatusCode.UnsupportedMediaType, "unsupported_media_type")]
    [InlineData(null, null, "application/x-www-form-urlencoded", HttpStatusCode.UnsupportedMediaType, "unsupported_media_type")]
    [InlineData(null, null, "multipart/form-data; boundary=x", HttpStatusCode.UnsupportedMediaType, "unsupported_media_type")]
    public async Task ACheckpointCantBeMadeFromAnotherSiteOrWithoutJson(string? header, string? value, string type, HttpStatusCode expected, string code)
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        den.Write(MinaFile, den.Read(MinaFile) + "\nMore.\n");
        await using var server = await StartAsync(den);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkpoint")
        {
            Content = new StringContent("""{"message": "From elsewhere"}""", Encoding.UTF8),
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
        if (header is not null)
        {
            request.Headers.Add(header, value);
        }

        using var response = await server.Client.SendAsync(request, Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal(expected, response.StatusCode);
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("1", RunGit(den, "rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task OtherMethodsAreRefusedWithTheOnesItTakes()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        using var response = await server.Client.PutAsync("/api/checkpoint", new StringContent("{}", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET, POST", string.Join(", ", response.Content.Headers.Allow));

        var query = await server.GetJsonAsync("/api/checkpoint?universe=dracula", HttpStatusCode.BadRequest);
        Assert.Equal("bad_argument", query.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-52")]
    public async Task AHookThatRefusesIsReportedAndNothingIsRecorded()
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        den.Write(MinaFile, den.Read(MinaFile) + "\nMore.\n");
        var hook = Path.Combine(den.Root, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'spell check failed' >&2\nexit 1\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await using var server = await StartAsync(den);
        var (status, json) = await PostAsync(server, """{"message": "Planning session: harbour subplot"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("history_failed", json.GetProperty("code").GetString());
        var message = json.GetProperty("message").GetString()!;
        Assert.Contains("refused", message, StringComparison.Ordinal);
        Assert.Contains("hook", message, StringComparison.Ordinal);
        Assert.Equal("1", RunGit(den, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal(1, (await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK)).GetProperty("count").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-54")]
    public async Task APinnedServerCheckpointsTheWholeDenButNamesOnlyItsUniverse()
    {
        var den = Repository(TempDen.Starfall());
        Commit(den, "Start");
        den.Write("universes/starfall/entities/locations/kessan-reach.md", den.Read("universes/starfall/entities/locations/kessan-reach.md") + "\nMore.\n");
        den.Write("universes/drift/universe.md", den.Read("universes/drift/universe.md") + "\nA secret project.\n");
        den.Write("den.yaml", den.Read("den.yaml") + "\n# a comment\n");
        await using var server = await Server.StartAsync(
            new WolfdenApiOptions(den.Root) { PinnedUniverse = "starfall", History = () => GitHistory.ForDen(den.Root, Git) });

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(3, status.GetProperty("count").GetInt32());
        Assert.Equal(1, status.GetProperty("otherUniverses").GetInt32());
        Assert.Equal(
            ["den.yaml", "universes/starfall/entities/locations/kessan-reach.md"],
            status.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()));
        Assert.DoesNotContain("drift", status.ToString(), StringComparison.Ordinal);

        var (code, made) = await PostAsync(server, """{"message": "Notes pass after drafting ch 12"}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(3, made.GetProperty("checkpoint").GetProperty("files").GetInt32());
        Assert.Equal(1, made.GetProperty("otherUniverses").GetInt32());
        Assert.DoesNotContain("drift", made.ToString(), StringComparison.Ordinal);

        // The whole den was recorded, the other universe's file included.
        Assert.Contains("universes/drift/universe.md", RunGit(den, "show", "--name-only", "--format=", "HEAD"), StringComparison.Ordinal);
        Assert.Equal(0, (await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK)).GetProperty("count").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-54")]
    public async Task APinnedServersLatestCheckpointDoesntNameAnotherUniverse()
    {
        var den = Repository(TempDen.Starfall());
        Commit(den, "Start");
        den.Write("universes/drift/universe.md", den.Read("universes/drift/universe.md") + "\nA secret project.\n");
        Commit(den, "Drift: the tide subplot in universes/drift");
        await using var server = await Server.StartAsync(
            new WolfdenApiOptions(den.Root) { PinnedUniverse = "starfall", History = () => GitHistory.ForDen(den.Root, Git) });

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.String, status.GetProperty("latest").GetProperty("id").ValueKind);
        Assert.DoesNotContain("drift", status.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-52")]
    public async Task AManualCheckpointOnADetachedHeadIsMadeButWarned()
    {
        var den = Repository(TempDen.Dracula());
        Commit(den, "Start");
        RunGit(den, "checkout", "--quiet", "--detach");
        den.Write(MinaFile, den.Read(MinaFile) + "\nMore.\n");
        await using var server = await StartAsync(den);

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.StartsWith("a detached HEAD", status.GetProperty("inProgress").GetString(), StringComparison.Ordinal);

        var (code, made) = await PostAsync(server, """{"message": "Notes pass after drafting ch 12"}""");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(made.GetProperty("recorded").GetBoolean());
        Assert.StartsWith("a detached HEAD", made.GetProperty("inProgress").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-53")]
    [InlineData("auto_checkpoint: 1h30m\n", "1h30m", false)]
    [InlineData("auto_checkpoint: soon\n", null, true)]
    [InlineData("", null, false)]
    public async Task TheStatusSaysWhatAutoCheckpointIsSetTo(string line, string? every, bool problem)
    {
        var den = Repository(TempDen.Dracula());
        den.Write("den.yaml", den.Read("den.yaml") + line);
        await using var server = await Server.StartAsync(
            new WolfdenApiOptions(den.Root) { History = () => GitHistory.ForDen(den.Root, Git), AutoCheckpoints = true });

        var auto = (await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK)).GetProperty("autoCheckpoint");
        Assert.Equal(every, auto.GetProperty("every").GetString());
        Assert.Equal(problem, auto.GetProperty("problem").ValueKind == JsonValueKind.String);
        Assert.True(auto.GetProperty("runs").GetBoolean());
    }

    // ------------------------------------------------------------ helpers

    private TempDen Den(TempDen den)
    {
        _dens.Add(den);
        return den;
    }

    private TempDen Repository(TempDen den)
    {
        Den(den);
        RunGit(den, "init", "--quiet");
        return den;
    }

    private Task<Server> StartAsync(TempDen den) =>
        Server.StartAsync(new WolfdenApiOptions(den.Root) { History = () => GitHistory.ForDen(den.Root, Git) });

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(Server server, string body)
    {
        using var response = await server.Client.PostAsync("/api/checkpoint", new StringContent(body, Encoding.UTF8, "application/json"), Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private string Commit(TempDen den, string message)
    {
        RunGit(den, "add", "--all");
        RunGit(den, "commit", "--quiet", "-m", message);
        return RunGit(den, "rev-parse", "HEAD").Trim();
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
