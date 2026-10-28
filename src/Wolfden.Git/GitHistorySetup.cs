using System.Text;
using Wolfden.Core.History;

namespace Wolfden.Git;

/// <summary>
/// Turns git history on for a den that has none (#195): <c>git init</c> in
/// the den's folder, <c>.wolfden/</c> in its <c>.gitignore</c>, a name and
/// email saved in the repository's own config if git has none, and a first
/// checkpoint. See <see cref="IHistorySetup"/> for the rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name and email</b> are what git itself would use for the den's
/// repository: <c>git config user.name</c> and <c>user.email</c> run in the
/// den's folder, so the user's global and system settings count. They're
/// asked for only when one is missing, and saved with <c>git config
/// --local</c>, in that repository only, never globally; an existing
/// setting is never changed. They're checked before anything is made, so a
/// missing one refuses with nothing changed, and again once the repository
/// exists, since a setting can depend on where the repository is (git's
/// <c>includeIf "gitdir:…"</c>), and only a missing one is saved.
/// </para>
/// <para>
/// <b>.gitignore</b> is created if there's none, and otherwise
/// <c>.wolfden/</c> is added at its end if no line already names it; the
/// rest of the file is never changed.
/// </para>
/// </remarks>
public sealed class GitHistorySetup : IHistorySetup
{
    /// <summary>The line the den's <c>.gitignore</c> needs.</summary>
    public const string IgnoreLine = ".wolfden/";

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly string[] IgnoreSpellings = [".wolfden", ".wolfden/", "/.wolfden", "/.wolfden/", ".wolfden/*", "/.wolfden/*", ".wolfden/**", "/.wolfden/**"];

    private readonly string _denRoot;
    private readonly GitOptions _options;

    /// <summary>Turning history on for the den at <paramref name="denRoot"/>, with <paramref name="options"/>' git.</summary>
    public GitHistorySetup(string denRoot, GitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        _denRoot = Path.GetFullPath(denRoot);
        _options = options ?? GitOptions.Default;
    }

    /// <inheritdoc />
    public HistorySetupCheck Check(CancellationToken cancellationToken = default)
    {
        var location = GitRepository.Locate(_denRoot, _options, cancellationToken);
        var finishing = false;
        switch (location.Kind)
        {
            case GitLocationKind.GitNotInstalled:
                return Refuse(
                    HistorySetupCodes.GitNotInstalled,
                    "Git isn't installed, or Wolfden can't find it on the PATH, so history can't be turned on. Install git, then try again.");
            case GitLocationKind.Unusable:
                return Refuse(HistorySetupCodes.HistoryFailed, $"Git can't use the den's folder ({location.Problem}), so history can't be turned on. Fix what git says, then try again.");
            case GitLocationKind.InRepository when !HasOwnGit():
                return Refuse(
                    HistorySetupCodes.InRepository,
                    $"The den is inside the git repository at '{location.Root}', which keeps its history already, for the den's files only. There's nothing to turn on.");
            case GitLocationKind.InRepository:
                bool hasCommits;
                try
                {
                    hasCommits = HasCommits(cancellationToken);
                }
                catch (HistoryException e)
                {
                    return Refuse(HistorySetupCodes.HistoryFailed, e.Message);
                }

                if (hasCommits)
                {
                    return Refuse(
                        HistorySetupCodes.HasRepository,
                        "The den's folder is already a git repository with checkpoints, so its history is on. Nothing was changed.");
                }

                finishing = true;
                break;
            case GitLocationKind.NotARepository when HasOwnGit():
                return Refuse(
                    HistorySetupCodes.HistoryFailed,
                    "The den's folder has a .git that git doesn't take for a repository, so Wolfden won't make another. Move it out of the way, then try again.");
        }

        try
        {
            var (name, email) = Identity(cancellationToken);
            return new HistorySetupCheck(true, null, null, name is null, email is null, finishing);
        }
        catch (HistoryException e)
        {
            return Refuse(HistorySetupCodes.HistoryFailed, e.Message);
        }

        static HistorySetupCheck Refuse(string code, string reason) => new(false, code, reason, false, false);
    }

    /// <inheritdoc />
    public HistorySetupResult TurnOn(HistorySetupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.AuthorName?.Trim();
        var email = request.AuthorEmail?.Trim();
        if ((name is null ? null : HistorySetup.IdentityProblem("name", name)) is { } nameProblem)
        {
            return Refused(HistorySetupCodes.BadArgument, nameProblem);
        }

        if ((email is null ? null : HistorySetup.IdentityProblem("email", email)) is { } emailProblem)
        {
            return Refused(HistorySetupCodes.BadArgument, emailProblem);
        }

        var check = Check(cancellationToken);
        if (!check.Possible)
        {
            return Refused(check.Code!, check.Reason!);
        }

        if (Missing(check.NeedsName, check.NeedsEmail, name, email) is { } missing)
        {
            return new HistorySetupResult(
                HistorySetupStatus.Refused, HistorySetupCodes.IdentityNeeded, $"{missing} Nothing was changed.", [], null, false, null, [], null,
                check.NeedsName && name is null, check.NeedsEmail && email is null);
        }

        var steps = new List<string>();
        var made = false;
        string? gitIgnore = null;
        var saved = new List<string>();

        // 1. The repository.
        if (!check.Finishing)
        {
            var created = GitRepository.Create(_denRoot, _options, cancellationToken);
            if (created.Kind != GitLocationKind.InRepository)
            {
                return Stop(
                    created.Kind == GitLocationKind.GitNotInstalled ? HistorySetupCodes.GitNotInstalled : HistorySetupCodes.HistoryFailed,
                    $"Git couldn't make the den's folder a repository: {created.Problem}.",
                    "Nothing else was done.");
            }

            made = true;
            steps.Add("Made the den's folder a git repository.");
        }
        else
        {
            steps.Add("Used the git repository already in the den's folder, which had no checkpoints yet.");
        }

        // 2. .gitignore.
        try
        {
            gitIgnore = EnsureIgnored();
            steps.Add(gitIgnore switch
            {
                "created" => $"Created .gitignore, with {IgnoreLine}, so Wolfden's index stays out of the history.",
                "added" => $"Added {IgnoreLine} to .gitignore, so Wolfden's index stays out of the history.",
                _ => $".gitignore already leaves out {IgnoreLine}.",
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Stop(HistorySetupCodes.WriteFailed, $"Couldn't update .gitignore: {e.Message}.", NotDone(identity: true));
        }

        // 3. The name and email, if git has none for the repository.
        try
        {
            var (hasName, hasEmail) = Identity(cancellationToken);
            if (Missing(hasName is null, hasEmail is null, name, email) is { } stillMissing)
            {
                return Stop(HistorySetupCodes.IdentityNeeded, stillMissing, NotDone(identity: true), hasName is null && name is null, hasEmail is null && email is null);
            }

            if (hasName is null)
            {
                SetLocal("user.name", name!, cancellationToken);
                saved.Add("user.name");
            }

            if (hasEmail is null)
            {
                SetLocal("user.email", email!, cancellationToken);
                saved.Add("user.email");
            }
        }
        catch (HistoryException e)
        {
            return Stop(HistorySetupCodes.HistoryFailed, $"Couldn't save the name and email for the den's repository: {e.Message}", NotDone(identity: true));
        }

        if (saved.Count > 0)
        {
            var what = saved.Count == 2 ? "your name and email" : saved[0] == "user.name" ? "your name" : "your email";
            steps.Add($"Saved {what} for this den's repository only.");
        }

        // 4. The first checkpoint.
        HistoryEntry? first = null;
        if (!request.FirstCheckpoint)
        {
            steps.Add("Made no first checkpoint, as asked.");
        }
        else
        {
            try
            {
                var history = GitHistory.ForDen(_denRoot, _options, cancellationToken);
                var checkpoint = new CheckpointRequest(HistorySetup.FirstCheckpointMessage);
                var result = request.Checkpoint is { } make ? make(history, checkpoint) : history.Checkpoint(checkpoint, cancellationToken);
                if (!result.IsAvailable)
                {
                    return Stop(HistorySetupCodes.HistoryFailed, $"Couldn't make the first checkpoint: {result.Reason}", FirstCheckpointNotDone);
                }

                first = result.Value;
                steps.Add(first is null
                    ? "Made no first checkpoint: the den has no files to record yet."
                    : $"Made the first checkpoint, \"{HistorySetup.FirstCheckpointMessage}\", of {Count(first.Changes.Count)}.");
            }
            catch (HistoryException e)
            {
                return Stop(HistorySetupCodes.HistoryFailed, $"Couldn't make the first checkpoint: {e.Message}", FirstCheckpointNotDone);
            }
        }

        return new HistorySetupResult(HistorySetupStatus.Done, null, null, steps, _denRoot, made, gitIgnore, saved, first);

        HistorySetupResult Refused(string code, string problem) => new(HistorySetupStatus.Refused, code, problem, [], null, false, null, [], null);

        HistorySetupResult Stop(string code, string problem, string notDone, bool needsName = false, bool needsEmail = false)
        {
            var status = steps.Count == 0 && !made ? HistorySetupStatus.Refused : HistorySetupStatus.Incomplete;
            var text = status == HistorySetupStatus.Refused ? $"{problem} {notDone}" : $"{problem} {notDone} {HowToFinish}";
            return new HistorySetupResult(status, code, text, steps, steps.Count == 0 && !made ? null : _denRoot, made, gitIgnore, saved, null, needsName, needsEmail);
        }

        string NotDone(bool identity) => request.FirstCheckpoint
            ? identity ? "No name or email was saved, and no first checkpoint was made." : "No first checkpoint was made."
            : "No name or email was saved.";
    }

    private const string FirstCheckpointNotDone = "No first checkpoint was made, but history is on.";

    /// <summary>What a turn-on that stopped part way tells the writer to do.</summary>
    private const string HowToFinish =
        "The den's folder is a git repository now, so to finish, fix the problem and run 'wolfden init --git' in the den again: it picks up where this stopped. " +
        "Or make the first checkpoint from the web UI's checkpoint dialog.";

    private static string Count(int files) => files == 1 ? "1 file" : $"{files} files";

    /// <summary>What's missing that wasn't given, in words, or null if git will have both.</summary>
    private static string? Missing(bool needsName, bool needsEmail, string? name, string? email)
    {
        var noName = needsName && name is null;
        var noEmail = needsEmail && email is null;
        if (!noName && !noEmail)
        {
            return null;
        }

        var what = noName && noEmail ? "a name and an email" : noName ? "a name" : "an email";
        return $"Git records who made each checkpoint, and it has no {(noName && noEmail ? "name or email" : noName ? "name" : "email")} for this den. " +
            $"Give {what}: they're saved for this den's repository only. Any name will do, a pen name included, and the email can be a placeholder, " +
            "such as you@example.com.";
    }

    private bool HasOwnGit()
    {
        var git = Path.Combine(_denRoot, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    private bool HasCommits(CancellationToken cancellationToken)
    {
        var run = Run(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken);
        return run.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new HistoryException($"Git couldn't tell whether the den's repository has checkpoints: {run.Said}"),
        };
    }

    /// <summary>The name and email git would use for the den's repository, each null if it has none.</summary>
    private (string? Name, string? Email) Identity(CancellationToken cancellationToken) => (Setting("user.name", cancellationToken), Setting("user.email", cancellationToken));

    private string? Setting(string key, CancellationToken cancellationToken)
    {
        var run = Run(["config", "--get", key], cancellationToken);
        if (run.ExitCode == 1)
        {
            return null;
        }

        if (!run.Succeeded)
        {
            throw new HistoryException($"Git couldn't read its '{key}' setting: {run.Said}");
        }

        var value = run.Output.Trim();
        return value.Length == 0 ? null : value;
    }

    private void SetLocal(string key, string value, CancellationToken cancellationToken)
    {
        var run = Run(["config", "--local", key, value], cancellationToken, GitCallKind.MustFinish);
        if (!run.Succeeded)
        {
            throw new HistoryException($"git config {key} failed: {run.Said}");
        }
    }

    private GitRun Run(string[] args, CancellationToken cancellationToken, GitCallKind kind = GitCallKind.Stoppable)
    {
        try
        {
            return GitProcess.Run(_options, _denRoot, args, cancellationToken: cancellationToken, kind: kind);
        }
        catch (GitNotFoundException e)
        {
            throw new HistoryException($"Git isn't installed, or Wolfden can't find it on the PATH: {e.Message}", e);
        }
        catch (GitTimeoutException e)
        {
            throw new HistoryException($"{e.Message}, so Wolfden stopped waiting for it", e);
        }
    }

    /// <summary>Makes sure the den's <c>.gitignore</c> leaves out <c>.wolfden/</c>: <c>created</c>, <c>added</c>, or <c>present</c>.</summary>
    private string EnsureIgnored()
    {
        var path = Path.Combine(_denRoot, ".gitignore");
        if (!File.Exists(path))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Utf8.GetBytes($"# Wolfden's index, rebuilt from the den's files\n{IgnoreLine}\n"));
                return "created";
            }
            catch (IOException) when (File.Exists(path))
            {
                // Made meanwhile: fall through and read it.
            }
        }

        var text = File.ReadAllText(path);
        var lines = text.Split('\n').Select(l => l.Trim());
        if (lines.Any(l => IgnoreSpellings.Contains(l, StringComparer.Ordinal)))
        {
            return "present";
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var start = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        File.AppendAllText(path, $"{start}{IgnoreLine}{newline}", Utf8);
        return "added";
    }
}
