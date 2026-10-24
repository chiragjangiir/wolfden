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
}
