using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Wolfden.Git;

namespace Wolfden.Api.Tests;

/// <summary>
/// Turning history on from the checkpoint dialog (#195): <c>GET
/// /api/checkpoint</c> says whether it can be, and <c>POST
/// /api/history/turn-on</c> does it, through git, over a copy of a sample
/// den. Git runs with a configuration of the tests' own, never the
/// developer's: one with a name and email, or one without, for the dialog's
/// name and email fields.
/// </summary>
public sealed class HistorySetupEndpointTests : IDisposable
{
    private const string MinaFile = "universes/dracula/books/dracula/entities/characters/mina-harker.md";

    private const string WithIdentity =
        "[user]\n\tname = Test Writer\n\temail = writer@example.com\n[commit]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n";

    private static readonly string TestsFolder = Path.Combine(Path.GetTempPath(), "wolfden-api-tests");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<TempDen> _dens = [];
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var den in _dens)
        {
            MakeDeletable(den.Root);
            den.Dispose();
        }

        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-55")]
    [Trait("Scenario", "SC-HIST-14")]
    public async Task TheDialogTurnsHistoryOnAndTheRunningServerUsesItAtOnce()
    {
        var den = Den(TempDen.Dracula());
        var git = Git(WithIdentity);
        await using var server = await StartAsync(den, git);

        var before = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.False(before.GetProperty("available").GetBoolean());
        Assert.Contains("Turn on history", before.GetProperty("enable").GetString(), StringComparison.Ordinal);
        var turnOn = before.GetProperty("turnOn");
        Assert.True(turnOn.GetProperty("possible").GetBoolean(), turnOn.ToString());
        Assert.False(turnOn.GetProperty("needsName").GetBoolean());
        Assert.False(turnOn.GetProperty("needsEmail").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(server, "/api/checkpoint", """{"message": "Before"}""")).Status);

        var (code, made) = await Post(server, "/api/history/turn-on", "{}");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("git", made.GetProperty("backend").GetString());
        Assert.True(made.GetProperty("repositoryMade").GetBoolean());
        Assert.Equal("present", made.GetProperty("gitIgnore").GetString()); // the sample has one
        Assert.Equal(0, made.GetProperty("identitySaved").GetArrayLength());
        Assert.Equal("Made the den's folder a git repository.", made.GetProperty("steps")[0].GetString());
        var first = made.GetProperty("checkpoint");
        Assert.Equal("Start of history", first.GetProperty("message").GetString());
        Assert.Equal("Test Writer", first.GetProperty("author").GetString());
        Assert.True(first.GetProperty("files").GetInt32() > 10);
        Assert.Equal("# Wolfden's index and local state (format.md F-LAY-2)\n.wolfden/\n", den.Read(".gitignore").ReplaceLineEndings("\n"));

        // Without a restart: the status, the History tab, and the next checkpoint all use it.
        var after = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.True(after.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("turnOn").ValueKind);
        Assert.Equal(0, after.GetProperty("count").GetInt32());
        Assert.Equal(first.GetProperty("id").GetString(), after.GetProperty("latest").GetProperty("id").GetString());
        var history = await server.GetJsonAsync("/api/history?entity=Mina%20Harker", HttpStatusCode.OK);
        Assert.Equal(first.GetProperty("id").GetString(), history.GetProperty("entries")[0].GetProperty("id").GetString());
        den.Write(MinaFile, den.Read(MinaFile) + "\nShe keeps a diary in shorthand.\n");
        var (nextCode, next) = await Post(server, "/api/checkpoint", """{"message": "Notes pass"}""");
        Assert.Equal(HttpStatusCode.OK, nextCode);
        Assert.True(next.GetProperty("recorded").GetBoolean());

        // Again: it's on, so it's refused, and nothing changes.
        var (againCode, again) = await Post(server, "/api/history/turn-on", "{}");
        Assert.Equal(HttpStatusCode.Conflict, againCode);
        Assert.Equal("has_repository", again.GetProperty("code").GetString());
        Assert.Equal("2", RunGit(den, git, "rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-55")]
    public async Task WithoutTheFirstCheckpointEveryNoteIsLeftToCheckpoint()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await StartAsync(den, Git(WithIdentity));

        var (code, made) = await Post(server, "/api/history/turn-on", """{"firstCheckpoint": false}""");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(JsonValueKind.Null, made.GetProperty("checkpoint").ValueKind);
        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("latest").ValueKind);
        Assert.True(status.GetProperty("count").GetInt32() > 10);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-55")]
    [Trait("Scenario", "SC-HIST-15")]
    public async Task AMissingNameAndEmailAreAskedForAndSavedForTheDenOnly()
    {
        var den = Den(TempDen.Dracula());
        var global = Config("");
        var git = Git(global: global);
        await using var server = await StartAsync(den, git);

        var turnOn = (await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK)).GetProperty("turnOn");
        Assert.True(turnOn.GetProperty("possible").GetBoolean(), turnOn.ToString());
        Assert.True(turnOn.GetProperty("needsName").GetBoolean());
        Assert.True(turnOn.GetProperty("needsEmail").GetBoolean());
        Assert.False(turnOn.GetProperty("identityCertain").GetBoolean()); // before git init, includeIf "gitdir:" can't apply

        var (refusedCode, refused) = await Post(server, "/api/history/turn-on", """{"firstCheckpoint": true}""");
        Assert.Equal(HttpStatusCode.BadRequest, refusedCode);
        Assert.Equal("identity_needed", refused.GetProperty("code").GetString());
        Assert.Contains("placeholder", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));

        var (code, made) = await Post(server, "/api/history/turn-on", """{"authorName": "Ash Calloway", "authorEmail": "ash@example.invalid"}""");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(["user.name", "user.email"], made.GetProperty("identitySaved").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Ash Calloway", made.GetProperty("checkpoint").GetProperty("author").GetString());
        Assert.Equal("Ash Calloway\n", RunGit(den, git, "config", "--local", "--get", "user.name"));
        Assert.Equal("", File.ReadAllText(global));
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-55")]
    [InlineData("""{"authorName": "Ash\nCalloway"}""", "one line")]
    [InlineData("""{"authorEmail": "<ash@example.invalid>"}""", "'<' or '>'")]
    [InlineData("""{"authorName": 12}""", "must be a string")]
    [InlineData("""{"firstCheckpoint": "yes"}""", "true or false")]
    [InlineData("""{"message": "Start"}""", "doesn't take 'message'")]
    [InlineData("""["firstCheckpoint"]""", "JSON object")]
    [InlineData("not json", "isn't valid JSON")]
    public async Task ABadRequestIsRefusedWithNothingChanged(string body, string says)
    {
        var den = Den(TempDen.Dracula());
        await using var server = await StartAsync(den, Git(WithIdentity));

        var (code, json) = await Post(server, "/api/history/turn-on", body);

        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("bad_argument", json.GetProperty("code").GetString());
        Assert.Contains(says, json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));
    }

    [Fact]
    public async Task ATooLongNameIsRefused()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await StartAsync(den, Git(WithIdentity));

        var (code, json) = await Post(server, "/api/history/turn-on", JsonSerializer.Serialize(new { authorName = new string('a', 201) }));

        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("The name is at most 200 characters, not 201.", json.GetProperty("message").GetString());
    }

    [Theory]
    [Trait("Scenario", "SC-WEB-3")]
    [InlineData("Origin", "https://attacker.example", "application/json", HttpStatusCode.Forbidden, "cross_site")]
    [InlineData("Sec-Fetch-Site", "cross-site", "application/json", HttpStatusCode.Forbidden, "cross_site")]
    [InlineData(null, null, "text/plain", HttpStatusCode.UnsupportedMediaType, "unsupported_media_type")]
    [InlineData(null, null, "application/x-www-form-urlencoded", HttpStatusCode.UnsupportedMediaType, "unsupported_media_type")]
    public async Task HistoryCantBeTurnedOnFromAnotherSiteOrWithoutJson(string? header, string? value, string type, HttpStatusCode expected, string code)
    {
        var den = Den(TempDen.Dracula());
        await using var server = await StartAsync(den, Git(WithIdentity));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/history/turn-on") { Content = new StringContent("{}", Encoding.UTF8) };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
        if (header is not null)
        {
            request.Headers.Add(header, value);
        }

        using var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains($"\"code\":\"{code}\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));
    }

    [Fact]
    public async Task OnlyPostIsTakenAndAQueryIsRefused()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await StartAsync(den, Git(WithIdentity));

        var get = await server.GetJsonAsync("/api/history/turn-on", HttpStatusCode.MethodNotAllowed);
        Assert.Equal("method_not_allowed", get.GetProperty("code").GetString());
        var (code, json) = await Post(server, "/api/history/turn-on?firstCheckpoint=false", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("bad_argument", json.GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(den.Root, ".git")));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-17")]
    public async Task ADenInsideALargerRepositoryIsRefused()
    {
        var writing = Den(TempDen.Dracula());
        var git = Git(WithIdentity);
        RunGit(writing, git, "init", "--quiet");
        writing.Write("notes/kestrel/den.yaml", "format: 1\n");
        var root = Path.Combine(writing.Root, "notes", "kestrel");
        await using var server = await Server.StartAsync(
            new WolfdenApiOptions(root) { History = () => GitHistory.ForDen(root, git), HistorySetup = new GitHistorySetup(root, git) });

        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("turnOn").ValueKind);
        var (code, json) = await Post(server, "/api/history/turn-on", "{}");

        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.Equal("in_repository", json.GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(root, ".gitignore")));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-55")]
    public async Task AServerThatCantTurnItOnSaysSo()
    {
        var den = Den(TempDen.Dracula());
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var turnOn = (await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK)).GetProperty("turnOn");
        Assert.False(turnOn.GetProperty("possible").GetBoolean());
        Assert.Contains("wolfden init --git", turnOn.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var (code, json) = await Post(server, "/api/history/turn-on", "{}");
        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.Equal("history_unavailable", json.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-55")]
    [Trait("Scenario", "SC-WEB-54")]
    public async Task APinnedServerTurnsItOnForTheWholeDenAndNamesNoFiles()
    {
        var den = Den(TempDen.Starfall());
        var git = Git(WithIdentity);
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root)
        {
            PinnedUniverse = "starfall",
            History = () => GitHistory.ForDen(den.Root, git),
            HistorySetup = new GitHistorySetup(den.Root, git),
        });

        var (code, made) = await Post(server, "/api/history/turn-on", "{}");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.DoesNotContain("drift", made.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("universes/drift/universe.md", RunGit(den, git, "show", "--name-only", "--format=", "HEAD"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-16")]
    public async Task AFailedFirstCheckpointSaysWhatWasDoneAndTheDialogCanFinishIt()
    {
        var den = Den(TempDen.Dracula());
        var hooks = Path.Combine(TestsFolder, $"hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\necho 'notes must be proofread first' >&2\nexit 1\n", new UTF8Encoding(false));
        _files.Add(hook);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var git = Git(WithIdentity + $"[core]\n\thooksPath = {hooks.Replace('\\', '/')}\n");
        await using var server = await StartAsync(den, git);

        var (code, json) = await Post(server, "/api/history/turn-on", "{}");

        Assert.Equal(HttpStatusCode.InternalServerError, code);
        Assert.Equal("history_failed", json.GetProperty("code").GetString());
        var message = json.GetProperty("message").GetString()!;
        Assert.StartsWith("Made the den's folder a git repository. .gitignore already leaves out .wolfden/.", message, StringComparison.Ordinal);
        Assert.Contains("notes must be proofread first", message, StringComparison.Ordinal);
        Assert.Contains("checkpoint dialog", message, StringComparison.Ordinal);

        // History is on: the dialog now offers the first checkpoint, which works once the hook is fixed.
        var status = await server.GetJsonAsync("/api/checkpoint", HttpStatusCode.OK);
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.True(status.GetProperty("count").GetInt32() > 10);
        File.Delete(hook);
        var (made, _) = await Post(server, "/api/checkpoint", """{"message": "Start of history"}""");
        Assert.Equal(HttpStatusCode.OK, made);
    }

    private TempDen Den(TempDen den)
    {
        _dens.Add(den);
        return den;
    }

    private string Config(string text)
    {
        Directory.CreateDirectory(TestsFolder);
        var file = Path.Combine(TestsFolder, $"gitconfig-{Guid.NewGuid():N}");
        File.WriteAllText(file, text, new UTF8Encoding(false));
        _files.Add(file);
        return file;
    }

    /// <summary>The tests' git, with <paramref name="text"/> as the global config, or the file <paramref name="global"/>.</summary>
    private GitOptions Git(string? text = null, string? global = null) => new()
    {
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = global ?? Config(text!),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CEILING_DIRECTORIES"] = TestsFolder,
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_INDEX_FILE"] = null,
            ["GIT_AUTHOR_NAME"] = null,
            ["GIT_AUTHOR_EMAIL"] = null,
            ["GIT_COMMITTER_NAME"] = null,
            ["GIT_COMMITTER_EMAIL"] = null,
            ["EMAIL"] = null,
        },
    };

    private static Task<Server> StartAsync(TempDen den, GitOptions git) =>
        Server.StartAsync(new WolfdenApiOptions(den.Root)
        {
            History = () => GitHistory.ForDen(den.Root, git),
            HistorySetup = new GitHistorySetup(den.Root, git),
        });

    private static async Task<(HttpStatusCode Status, JsonElement Json)> Post(Server server, string path, string body)
    {
        using var response = await server.Client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string RunGit(TempDen den, GitOptions git, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = den.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in git.Environment)
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
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Result}");
        }

        return output.Result.ReplaceLineEndings("\n");
    }

    private static void MakeDeletable(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
