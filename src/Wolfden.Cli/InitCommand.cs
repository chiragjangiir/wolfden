using System.Text;
using System.Text.Json.Serialization;
using Wolfden.Core.Documents;
using Wolfden.Core.History;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;
using Wolfden.Git;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden init [&lt;folder&gt;] [--name &lt;name&gt;] [--universe &lt;key&gt;] [--git] [--json]</c>:
/// makes a new den (format.md §5): a <c>den.yaml</c>, and with
/// <c>--universe</c> a first universe, so <c>wolfden check</c> passes on
/// it with no errors or warnings. With <c>--git</c>, it also turns on the
/// den's history (#122, #195), unless it's already inside a repository; on
/// a folder that's already a den, <c>--git</c> turns on its history.
/// </summary>
/// <remarks>
/// It creates the folder if needed and never overwrites anything: a folder
/// that already has a <c>den.yaml</c> (without <c>--git</c>), that is inside
/// a den, or that holds a den in a folder a few levels below it (dens don't
/// nest, F-LAY-1, and <see cref="DenLocation.FindBelow"/> says how far it
/// looks), is refused with exit code 2. Other files already in the
/// folder, such as a README or a <c>.git</c> folder, are left alone.
/// <para>
/// <c>--git</c> turns history on after the den is made, as
/// <see cref="GitHistorySetup"/> does: <c>git init</c>, <c>.wolfden/</c> in
/// <c>.gitignore</c>, a name and email for the repository if git has none
/// (<c>--author-name</c>, <c>--author-email</c>), and a first checkpoint,
/// "Start of history", unless <c>--no-checkpoint</c>. A new den already
/// inside a repository is left as it is, since a den can be a subfolder of
/// a larger repository and its history is scoped to the den's files.
/// Without git installed, a new den is still made, and the output says the
/// repository wasn't (exit 0). If git is there but a step fails, or git
/// needs a name or email that wasn't given, the new den is kept and the
/// command exits 2.
/// </para>
/// <para>
/// On an existing den, <c>--git</c> refuses (exit 2, nothing changed) a den
/// inside a larger repository, one whose own repository already has
/// checkpoints, and one where git isn't installed. A den whose own
/// repository has no checkpoints yet is finished, so running it again after
/// a step failed is safe.
/// </para>
/// </remarks>
internal static class InitCommand
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static readonly CommandSpec Spec = new(
        "init",
        "usage: wolfden init [<folder>] [--name <name>] [--universe <key>] [--git [--no-checkpoint] [--author-name <name>] [--author-email <email>]] [--json]",
        """
        Creates a new den: a den.yaml, and with --universe a first universe.
        With --git, it also turns on the den's history; in a folder that's
        already a den, --git turns on that den's history.

          <folder>          where to create it (made if it doesn't exist); by
                            default, the current folder
          --name <name>     the den's display name; by default, the folder's name
          --universe <key>  also create universes/<key>/universe.md; the key is
                            lowercase letters, digits, and hyphens, starting
                            with a letter
          --git             turn on history: make the folder a git repository,
                            add .wolfden/ to its .gitignore, and make a first
                            checkpoint, "Start of history"; a new den already
                            inside a repository is left as it is
          --no-checkpoint   with --git, make no first checkpoint
          --author-name <name>, --author-email <email>
                            with --git, the name and email git records on
                            checkpoints, when git has none: saved for this
                            den's repository only. Any name will do, a pen
                            name included; the email can be a placeholder
          --json            print one JSON document instead of text

        It never overwrites anything, and won't create a den inside another den
        or around one in the folders a few levels below. --git on an existing
        den refuses one inside a larger repository (which keeps its history
        already) or one whose repository already has checkpoints.
        Exits 0 when the den is created (with --git, even if git isn't
        installed: it says so) or its history turned on, and 2 if it couldn't
        be, or if a step of turning on history failed.
        """,
        ["--json", "--git", "--no-checkpoint"],
        ["--name", "--universe", "--author-name", "--author-email"],
        [],
        1,
        "give at most one folder.");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) => Run(args, output, error, CliApp.Git);

    /// <summary>Runs the command with <paramref name="git"/> for <c>--git</c>.</summary>
    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, GitOptions git)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        var folder = line.Arguments.Count > 0 ? line.Arguments[0] : ".";
        var name = line.Value("--name");
        var universe = line.Value("--universe");
        var authorName = line.Value("--author-name");
        var authorEmail = line.Value("--author-email");
        var withGit = line.Has("--git");
        if (name is not null && name.Trim().Length == 0)
        {
            return Fail("the name can't be empty.");
        }

        if (universe is not null && !Keys.IsContainerKey(universe))
        {
            return Fail($"'{universe}' isn't a valid universe key: use lowercase letters, digits, and single hyphens, starting with a letter, such as 'the-long-dark' (F-KEY-1, F-KEY-2).");
        }

        if (!withGit && (line.Has("--no-checkpoint") || authorName is not null || authorEmail is not null))
        {
            var given = line.Has("--no-checkpoint") ? "--no-checkpoint" : authorName is not null ? "--author-name" : "--author-email";
            return Fail($"{given} goes with --git, which turns on the den's history.");
        }

        if (authorName is not null && HistorySetup.IdentityProblem("name", authorName.Trim()) is { } nameProblem)
        {
            return Fail($"--author-name: {nameProblem}");
        }

        if (authorEmail is not null && HistorySetup.IdentityProblem("email", authorEmail.Trim()) is { } emailProblem)
        {
            return Fail($"--author-email: {emailProblem}");
        }

        if (File.Exists(folder))
        {
            return Fail($"'{folder}' is a file, not a folder.");
        }

        var root = Path.GetFullPath(folder);
        var request = new HistorySetupRequest(authorName?.Trim(), authorEmail?.Trim(), !line.Has("--no-checkpoint"));
        if (File.Exists(Path.Combine(root, DenLocation.DenFile)))
        {
            if (!withGit)
            {
                return Fail($"'{folder}' is already a den: it has a den.yaml. Nothing was changed. To turn on its history, run 'wolfden init --git' in it.");
            }

            if (name is not null || universe is not null)
            {
                return Fail($"'{folder}' is already a den, so --name and --universe don't apply: they're for a new den. Nothing was changed. To turn on its history, run 'wolfden init --git' without them.");
            }

            return TurnOnExisting(root, request, line.Has("--json"), output, error, git);
        }

        var parent = Directory.GetParent(root);
        if (parent is not null && DenLocation.FindAbove(parent.FullName) is { } outer)
        {
            return Fail($"'{folder}' is inside the den at '{outer}', and dens don't nest (F-LAY-1). Choose a folder outside it.");
        }

        if (Directory.Exists(root) && DenLocation.FindBelow(root) is { } inner)
        {
            return Fail($"'{folder}' already holds the den at '{inner}', and dens don't nest (F-LAY-1). Choose a folder that doesn't hold one, or first move that den's den.yaml out of the way, or into a hidden folder such as .old/, which Wolfden ignores.");
        }

        var created = new List<string>();
        // Everything this run creates, folders included, so a failure part way can undo it.
        var made = new List<string>();
        try
        {
            CreateFolder(root, made);
            WriteNew(root, DenLocation.DenFile, DenYaml(name), made);
            created.Add(DenLocation.DenFile);
            if (universe is not null)
            {
                var path = $"universes/{universe}/universe.md";
                CreateFolder(Path.Combine(root, "universes"), made);
                CreateFolder(Path.Combine(root, "universes", universe), made);
                WriteNew(root, path, UniverseFile(universe), made);
                created.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var left = Undo(made);
            var leftNote = left.Count == 0
                ? " Nothing was left behind."
                : $" These couldn't be removed and are still there: {string.Join(", ", left.Select(p => $"'{p}'"))}.";
            return Fail($"couldn't create the den at '{folder}': {e.Message}{leftNote}");
        }

        var repository = withGit ? MakeRepository(root, request, git) : null;
        if (repository?.GitIgnore == "created")
        {
            created.Add(".gitignore");
        }

        if (line.Has("--json"))
        {
            CliJson.Write(output, new InitReport(1, root, created, repository));
        }
        else
        {
            foreach (var file in created)
            {
                output.WriteLine($"created {file}");
            }

            output.WriteLine();
            output.WriteLine($"Created a den at '{root}'.");
            if (repository is { Outcome: not InitGitReport.Failed })
            {
                output.WriteLine(repository.Message);
            }
            else if (repository?.Steps is { Count: > 0 } done)
            {
                // Part way: what was done; the error says what wasn't.
                output.WriteLine(string.Join("\n", done));
            }

            var gitHint = repository is null ? " Wolfden doesn't run git unless asked (--git); 'wolfden init --git' in the den turns on its history." : "";
            output.WriteLine(universe is null
                ? $"Next: add a universe (format.md §6.2), then run 'wolfden check'.{gitHint}"
                : $"Next: add series, books, and entities (format.md §5.1), then run 'wolfden check'.{gitHint}");
        }

        if (repository is { Outcome: InitGitReport.Failed })
        {
            error.WriteLine($"wolfden init: {repository.Message}");
            return CliApp.ExitUsage;
        }

        return CliApp.ExitOk;

        int Fail(string message)
        {
            error.WriteLine($"wolfden init: {message}");
            return CliApp.ExitUsage;
        }
    }

    /// <summary>
    /// <c>--git</c> for a new den: turns on its history, unless it's already
    /// inside a repository (#122, #195). Nothing here undoes the den.
    /// </summary>
    private static InitGitReport MakeRepository(string root, HistorySetupRequest request, GitOptions git)
    {
        var location = GitRepository.Locate(root, git);
        switch (location.Kind)
        {
            case GitLocationKind.InRepository:
                return new InitGitReport(
                    InitGitReport.AlreadyInRepository,
                    location.Root,
                    $"It's already inside the git repository at '{location.Root}', so no new repository was made. " +
                    "Wolfden keeps the den's history there, for the den's files only.");
            case GitLocationKind.GitNotInstalled:
                return new(
                    InitGitReport.GitNotInstalled,
                    null,
                    "Git isn't installed, or Wolfden can't find it on the PATH, so the den wasn't made a repository. " +
                    "The den works without one; to keep its history, install git and run 'wolfden init --git' in the den's folder.");
            case GitLocationKind.Unusable:
                return new InitGitReport(
                    InitGitReport.Failed, null, $"the den was created, but git couldn't tell whether its folder is in a repository: {location.Problem}");
        }

        var result = new GitHistorySetup(root, git).TurnOn(request);
        if (result.Status == HistorySetupStatus.Done)
        {
            return Report(InitGitReport.Created, result, string.Join("\n", result.Steps));
        }

        if (result.Code == HistorySetupCodes.GitNotInstalled && result.Status == HistorySetupStatus.Refused)
        {
            return new(
                InitGitReport.GitNotInstalled,
                null,
                "Git isn't installed, or Wolfden can't find it on the PATH, so the den wasn't made a repository. " +
                "The den works without one; to keep its history, install git and run 'wolfden init --git' in the den's folder.");
        }

        var problem = result.Code == HistorySetupCodes.IdentityNeeded && result.Status == HistorySetupStatus.Refused
            ? IdentityNeeded(result, "in the den")
            : result.Problem!;
        return Report(InitGitReport.Failed, result, $"the den was created, but its history wasn't turned on: {problem}");
    }

    /// <summary><c>--git</c> on a folder that's already a den: turns on its history (#195).</summary>
    private static int TurnOnExisting(string root, HistorySetupRequest request, bool json, TextWriter output, TextWriter error, GitOptions git)
    {
        var result = new GitHistorySetup(root, git).TurnOn(request);
        var outcome = result.Status switch
        {
            HistorySetupStatus.Done => result.RepositoryMade ? InitGitReport.Created : InitGitReport.Finished,
            HistorySetupStatus.Refused => InitGitReport.Refused,
            _ => InitGitReport.Failed,
        };
        var problem = result.Code == HistorySetupCodes.IdentityNeeded && result.Status == HistorySetupStatus.Refused
            ? IdentityNeeded(result, "again")
            : result.Problem;
        var message = result.Status == HistorySetupStatus.Done
            ? $"Turned on history for the den at '{root}'.\n{string.Join("\n", result.Steps)}"
            : problem!;
        var report = Report(outcome, result, message);
        if (json)
        {
            CliJson.Write(output, new InitReport(1, root, result.GitIgnore == "created" ? [".gitignore"] : [], report));
        }
        else if (result.Status == HistorySetupStatus.Done)
        {
            output.WriteLine(message);
            output.WriteLine("Checkpoints work from now on: in the web UI ('wolfden serve', then Checkpoint…), or through an AI assistant.");
        }
        else if (result.Steps.Count > 0)
        {
            output.WriteLine(string.Join("\n", result.Steps));
        }

        if (result.Status == HistorySetupStatus.Done)
        {
            return CliApp.ExitOk;
        }

        error.WriteLine($"wolfden init: {problem}");
        return CliApp.ExitUsage;
    }

    /// <summary>The refusal when git has no name or email for the den, in the CLI's words.</summary>
    private static string IdentityNeeded(HistorySetupResult result, string where)
    {
        var what = result.NeedsName && result.NeedsEmail ? "name or email" : result.NeedsName ? "name" : "email";
        var options = string.Join(
            " ",
            new[] { result.NeedsName ? "--author-name <name>" : null, result.NeedsEmail ? "--author-email <email>" : null }.OfType<string>());
        return $"git records who made each checkpoint, and has no {what} for this den. Run 'wolfden init --git' {where} with {options}: " +
            "they're saved for this den's repository only, never globally. Any name will do, a pen name included, and the email can be a placeholder, " +
            "such as you@example.com. Nothing was changed in git.";
    }

    private static InitGitReport Report(string outcome, HistorySetupResult result, string message) => new(
        outcome,
        result.Repository,
        message,
        result.Code,
        result.Steps.Count > 0 ? result.Steps : null,
        result.GitIgnore,
        result.IdentitySaved.Count > 0 ? result.IdentitySaved : null,
        result.Checkpoint is { } first ? new InitCheckpoint(first.Id, first.Message, first.Changes.Count) : null);

    /// <summary>
    /// <c>den.yaml</c>: the format, the name if one was given, and the main
    /// optional settings as comments, so a writer can find them.
    /// </summary>
    internal static byte[] DenYaml(string? name)
    {
        var start = "# Wolfden den settings. The format is described in Wolfden's docs/format.md, section 5.2.\nformat: 1\n";
        // The editor quotes the name if it has to, so it stays a string.
        var text = name is null
            ? start + "# name: My Notes           # display name; defaults to the folder's name\n"
            : YamlFile.Parse(Utf8.GetBytes(start)).WithValue("name", YamlScalarValue.String(name)).Text;
        text += "# spoiler_checks: true     # flag links to things the reader hasn't met yet (F-DEN-7)\n";
        text += "# remote_images: true      # let the web UI load images from websites in notes (F-DEN-8)\n";
        text += "# auto_checkpoint: 30m     # wolfden serve checkpoints after this long without changes (F-DEN-9)\n";
        return Utf8.GetBytes(text);
    }

    /// <summary>A universe file with a new id, a name made from its key, and a line of overview to replace.</summary>
    internal static byte[] UniverseFile(string key)
    {
        var name = string.Join(' ', key.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return MarkdownFile.Parse(Utf8.GetBytes("---\n---\n\nWhat this universe is: its setting, and the works in it.\n"), requireFrontmatter: true)
            .WithId(Ids.New())
            .WithFrontmatterValue("name", YamlScalarValue.String(name))
            .ToBytes();
    }

    /// <summary>Creates a folder and any missing folders above it, recording in <paramref name="made"/> each one this run made, outermost first.</summary>
    private static void CreateFolder(string path, List<string> made)
    {
        var missing = new Stack<string>();
        for (var dir = Path.GetFullPath(path); !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!)
        {
            missing.Push(dir);
        }

        foreach (var dir in missing)
        {
            Directory.CreateDirectory(dir);
            made.Add(dir);
        }
    }

    /// <summary>Removes what this run made, newest first, and returns what couldn't be removed.</summary>
    private static List<string> Undo(List<string> made)
    {
        var left = new List<string>();
        for (var i = made.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(made[i]))
                {
                    File.Delete(made[i]);
                }
                else if (Directory.Exists(made[i]))
                {
                    Directory.Delete(made[i]);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add(made[i]);
            }
        }

        return left;
    }

    /// <summary>
    /// Writes a new file, failing if one is already there. The file goes into
    /// <paramref name="made"/> as soon as it exists, so a write that fails part
    /// way (a full disk, say) still leaves it to be undone.
    /// </summary>
    private static void WriteNew(string root, string path, byte[] bytes, List<string> made)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        made.Add(full);
        stream.Write(bytes);
    }

    /// <summary>The JSON shape of <c>wolfden init --json</c>.</summary>
    /// <param name="Version">The shape's version, 1.</param>
    /// <param name="Den">The den root's full path.</param>
    /// <param name="Created">The files created, relative to the den root, with <c>/</c>.</param>
    /// <param name="Git">With <c>--git</c>, what happened about the repository; otherwise null, and left out.</param>
    internal sealed record InitReport(
        int Version, string Den, IReadOnlyList<string> Created, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InitGitReport? Git = null);

    /// <summary>What <c>--git</c> did.</summary>
    /// <param name="Outcome">One of <see cref="Created"/>, <see cref="Finished"/>, <see cref="AlreadyInRepository"/>, <see cref="GitNotInstalled"/>, <see cref="Refused"/>, or <see cref="Failed"/>.</param>
    /// <param name="Repository">The repository's top folder, when the den is in one.</param>
    /// <param name="Message">What happened, in words.</param>
    /// <param name="Code">For a refusal or a failed step of turning history on, its code (such as <c>identity_needed</c>); otherwise left out.</param>
    /// <param name="Steps">The steps of turning history on that were done, in words; left out if none.</param>
    /// <param name="GitIgnore"><c>created</c>, <c>added</c>, or <c>present</c>: what happened to <c>.gitignore</c>; left out if that step wasn't reached.</param>
    /// <param name="IdentitySaved">The settings saved in the repository's own config (<c>user.name</c>, <c>user.email</c>); left out if none.</param>
    /// <param name="Checkpoint">The first checkpoint, if one was made; otherwise left out.</param>
    internal sealed record InitGitReport(
        string Outcome,
        string? Repository,
        string Message,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Steps = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GitIgnore = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? IdentitySaved = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InitCheckpoint? Checkpoint = null)
    {
        /// <summary>History is on: the folder is a new repository.</summary>
        public const string Created = "created";

        /// <summary>History is on: the den's own repository, which had no checkpoints, was used (#195).</summary>
        public const string Finished = "finished";

        /// <summary>An existing den's history wasn't turned on, and nothing was changed: see the code (#195).</summary>
        public const string Refused = "refused";

        /// <summary>The folder was already inside a repository, which was left as it was.</summary>
        public const string AlreadyInRepository = "already_in_repository";

        /// <summary>Git isn't installed, so no repository was made; the den was.</summary>
        public const string GitNotInstalled = "git_not_installed";

        /// <summary>A step of turning history on failed, or git needed a name or email that wasn't given; a new den was still made.</summary>
        public const string Failed = "failed";
    }

    /// <summary>The first checkpoint that turning history on made.</summary>
    /// <param name="Id">Its id (a commit hash).</param>
    /// <param name="Message">Its message, "Start of history".</param>
    /// <param name="Files">How many den files it recorded.</param>
    internal sealed record InitCheckpoint(string Id, string Message, int Files);
}
