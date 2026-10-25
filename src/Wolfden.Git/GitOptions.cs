namespace Wolfden.Git;

/// <summary>
/// How Wolfden runs git: which program, and any environment variables to
/// set or clear for it. The defaults run the user's own <c>git</c> from the
/// PATH with their own configuration, so commit signing, hooks, credentials,
/// and <c>safe.directory</c> all apply (maintainer decision, 2026-10-22).
/// </summary>
public sealed record GitOptions
{
    /// <summary>The defaults: <c>git</c> from the PATH, with the user's environment.</summary>
    public static GitOptions Default { get; } = new();

    /// <summary>The git program: a name looked up on the PATH, or a full path.</summary>
    public string Executable { get; init; } = "git";

    /// <summary>
    /// Environment variables to set for git, on top of the process's own; a
    /// null value removes one. Tests use it to give git a configuration of
    /// their own (<c>GIT_CONFIG_GLOBAL</c>, <c>GIT_CONFIG_NOSYSTEM</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// How long a git command that only reads (or writes a temporary index of
    /// Wolfden's own) may take before Wolfden stops it and says why (#143).
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits for ever.
    /// </summary>
    public TimeSpan ReadTimeout
    {
        get;
        init => field = CheckTimeout(value);
    } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a git command that writes the user's index, refs, or
    /// repository (a checkpoint's commit, a tag) may take before Wolfden stops
    /// waiting for it (#143). Commit signing can wait on a passphrase prompt
    /// and a hook can be slow, so it's generous. Git itself is left running
    /// to finish on its own, since stopping it part way would leave the
    /// repository locked (#145). <see cref="Timeout.InfiniteTimeSpan"/> waits
    /// for ever.
    /// </summary>
    public TimeSpan WriteTimeout
    {
        get;
        init => field = CheckTimeout(value);
    } = TimeSpan.FromMinutes(2);

    private static TimeSpan CheckTimeout(TimeSpan value) =>
        value == Timeout.InfiniteTimeSpan || (value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A git time limit must be more than zero, and less than about 24 days, or infinite.");
}
