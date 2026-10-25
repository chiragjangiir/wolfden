namespace Wolfden.Git;

/// <summary>Whether a folder is in a git repository that Wolfden can use.</summary>
public enum GitLocationKind
{
    /// <summary>The folder is in a repository's working tree.</summary>
    InRepository,

    /// <summary>The folder isn't in a git repository.</summary>
    NotARepository,

    /// <summary>Git isn't installed, or isn't on the PATH.</summary>
    GitNotInstalled,

    /// <summary>
    /// The folder looks like it's in a repository, but git refused to use it,
    /// for example because of its <c>safe.directory</c> rules.
    /// </summary>
    Unusable,
}

/// <summary>Where a folder stands with git.</summary>
/// <param name="Kind">Whether it's in a repository Wolfden can use.</param>
/// <param name="Root">The repository's top folder, as git gives it, when <paramref name="Kind"/> is <see cref="GitLocationKind.InRepository"/>.</param>
/// <param name="Problem">What went wrong, for <see cref="GitLocationKind.GitNotInstalled"/> and <see cref="GitLocationKind.Unusable"/>.</param>
public sealed record GitLocation(GitLocationKind Kind, string? Root = null, string? Problem = null);

/// <summary>Finding and making git repositories (#19, #122).</summary>
public static class GitRepository
{
    /// <summary>
    /// Whether <paramref name="folder"/> is in a git repository's working
    /// tree, as git itself decides (<c>git rev-parse --show-toplevel</c>), so
    /// <c>GIT_DIR</c>, <c>GIT_CEILING_DIRECTORIES</c>, and worktrees count.
    /// </summary>
    /// <param name="folder">An existing folder.</param>
    /// <param name="options">Which git to run, or null for the user's own.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public static GitLocation Locate(string folder, GitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        options ??= GitOptions.Default;
        GitRun run;
        try
        {
            run = GitProcess.Run(options, folder, ["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken);
        }
        catch (GitNotFoundException e)
        {
            return new GitLocation(GitLocationKind.GitNotInstalled, Problem: e.Message);
        }
        catch (GitTimeoutException e)
        {
            return new GitLocation(GitLocationKind.Unusable, Problem: $"{e.Message}, so Wolfden stopped it");
        }

        if (run.Succeeded && !run.OutputComplete)
        {
            // Without all of it, "no top folder" can't be told from "cut short".
            return new GitLocation(
                GitLocationKind.Unusable,
                Problem: "git rev-parse finished, but its output was still open when Wolfden stopped waiting, so Wolfden couldn't tell where the repository is");
        }

        if (run.Succeeded)
        {
            var top = run.Output.TrimEnd('\n', '\r');
            if (top.Length > 0)
            {
                return new GitLocation(GitLocationKind.InRepository, Path.GetFullPath(top));
            }

            // Inside a .git folder, or a bare repository: there's no working tree.
            return new GitLocation(GitLocationKind.NotARepository);
        }

        // Git's messages depend on the language it's set to, so tell "not a
        // repository" from "refused to use one" by looking for one instead.
        return HasGitAbove(folder, options)
            ? new GitLocation(GitLocationKind.Unusable, Problem: run.Said)
            : new GitLocation(GitLocationKind.NotARepository);
    }

    /// <summary>Makes <paramref name="folder"/> a new git repository, or says why not.</summary>
    /// <returns>The outcome: <see cref="GitLocationKind.InRepository"/> with the new repository's root, <see cref="GitLocationKind.GitNotInstalled"/>, or <see cref="GitLocationKind.Unusable"/> with what git said.</returns>
    public static GitLocation Create(string folder, GitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        options ??= GitOptions.Default;
        try
        {
            // The user's defaults apply: the branch name, templates, and hooks.
            // Never stopped part way, which would leave a half-made repository.
            var run = GitProcess.Run(options, folder, ["init", "--quiet"], cancellationToken: cancellationToken, kind: GitCallKind.MustFinish);
            if (!run.Succeeded)
            {
                return new GitLocation(GitLocationKind.Unusable, Problem: run.Said);
            }
        }
        catch (GitNotFoundException e)
        {
            return new GitLocation(GitLocationKind.GitNotInstalled, Problem: e.Message);
        }
        catch (GitTimeoutException e)
        {
            return new GitLocation(
                GitLocationKind.Unusable,
                Problem: $"{e.Message}. Wolfden left it running, since stopping it part way would leave a half-made repository");
        }

        return new GitLocation(GitLocationKind.InRepository, Path.GetFullPath(folder));
    }

    /// <summary>
    /// True if there's a <c>.git</c> in <paramref name="folder"/> or a folder
    /// above it that git would look in: like git, it doesn't go up into a
    /// folder named in <c>GIT_CEILING_DIRECTORIES</c>.
    /// </summary>
    private static bool HasGitAbove(string folder, GitOptions options)
    {
        var ceilingList = options.Environment.TryGetValue("GIT_CEILING_DIRECTORIES", out var set)
            ? set
            : Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ceilings = (ceilingList ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c)))
            .ToList();
        for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir is not null; dir = dir.Parent)
        {
            if (dir.FullName != Path.GetFullPath(folder) && ceilings.Any(c => string.Equals(c, Path.TrimEndingDirectorySeparator(dir.FullName), comparison)))
            {
                return false;
            }

            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return true;
            }
        }

        return false;
    }
}
