namespace Wolfden.Core.History;

/// <summary>
/// Turning history on for a den that has none (#195): <c>wolfden init
/// --git</c> on an existing den, and the checkpoint dialog's <b>Turn on
/// history</b>. Git's implementation is <c>GitHistorySetup</c> in
/// <c>Wolfden.Git</c>; a host without one can't turn history on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The steps, in order:</b> make the den's folder a repository; make sure
/// its <c>.gitignore</c> leaves out <c>.wolfden/</c>; save a name and email
/// for the repository if git has none; and, unless asked not to, make a
/// first checkpoint, <see cref="FirstCheckpointMessage"/>. A step that fails
/// stops the rest, and the result says what was done and what wasn't.
/// </para>
/// <para>
/// <b>Refused, with nothing changed:</b> a den inside a larger repository
/// (it already has history there), a den whose own repository already has
/// checkpoints, git missing or refusing the folder, and a missing name or
/// email that wasn't given.
/// </para>
/// <para>
/// <b>Finishing.</b> A den whose folder is the top of its own repository
/// with no checkpoints yet (one made by <c>git init</c>, or by a turn-on
/// that stopped part way) can be finished: every step but the first runs.
/// So trying again after a failure is safe.
/// </para>
/// </remarks>
public interface IHistorySetup
{
    /// <summary>Whether history can be turned on for the den now, and whether git needs a name or email first. It changes nothing.</summary>
    HistorySetupCheck Check(CancellationToken cancellationToken = default);

    /// <summary>Turns history on for the den, or says why not.</summary>
    HistorySetupResult TurnOn(HistorySetupRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Whether history can be turned on, and what git still needs.</summary>
/// <param name="Possible">True if <see cref="IHistorySetup.TurnOn"/> would go ahead (given a name and email, if they're needed).</param>
/// <param name="Code">Why not, as a code (<see cref="HistorySetupCodes"/>), or null.</param>
/// <param name="Reason">Why not, in words for the writer, or null.</param>
/// <param name="NeedsName">True if git has no <c>user.name</c> for the den's repository, so one must be given.</param>
/// <param name="NeedsEmail">True if git has no <c>user.email</c> for the den's repository, so one must be given.</param>
/// <param name="Finishing">True if the den already has its own repository with no checkpoints, so turning on finishes it rather than making one.</param>
public sealed record HistorySetupCheck(bool Possible, string? Code, string? Reason, bool NeedsName, bool NeedsEmail, bool Finishing = false);

/// <summary>What turning history on is asked to do.</summary>
/// <param name="AuthorName">The name to save for the den's repository if git has none, or null. Ignored when git already has one.</param>
/// <param name="AuthorEmail">The email to save if git has none, or null; a placeholder will do. Ignored when git already has one.</param>
/// <param name="FirstCheckpoint">True to finish with a first checkpoint of the den as it is (the default).</param>
public sealed record HistorySetupRequest(string? AuthorName = null, string? AuthorEmail = null, bool FirstCheckpoint = true)
{
    /// <summary>
    /// Makes the first checkpoint, in place of the history provider's own
    /// <see cref="IHistoryProvider.Checkpoint"/>: <c>wolfden serve</c> goes
    /// through its <see cref="CheckpointLedger"/>, as its other checkpoints do.
    /// </summary>
    public Func<IHistoryProvider, CheckpointRequest, HistoryResult<HistoryEntry?>>? Checkpoint { get; init; }
}

/// <summary>How turning history on ended.</summary>
public enum HistorySetupStatus
{
    /// <summary>Every step asked for was done.</summary>
    Done,

    /// <summary>Nothing was changed: see the result's code and problem.</summary>
    Refused,

    /// <summary>A step failed after others had been done: the result says which were.</summary>
    Incomplete,
}

/// <summary>What turning history on did.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Code">For a refusal or a failed step, a code (<see cref="HistorySetupCodes"/>); otherwise null.</param>
/// <param name="Problem">For a refusal or a failed step, what went wrong and what to do, in words; otherwise null.</param>
/// <param name="Steps">What was done, in order, in words for the writer.</param>
/// <param name="Repository">The repository's top folder, once the den is in one; otherwise null.</param>
/// <param name="RepositoryMade">True if this run made the repository (false when finishing one, or when it was refused).</param>
/// <param name="GitIgnore">What happened to <c>.gitignore</c>: <c>created</c>, <c>added</c> (the line was added to it), <c>present</c> (it already had it), or null if that step wasn't reached.</param>
/// <param name="IdentitySaved">The settings saved in the repository's own config: <c>user.name</c>, <c>user.email</c>, both, or none.</param>
/// <param name="Checkpoint">The first checkpoint, if one was made; otherwise null.</param>
/// <param name="NeedsName">For <see cref="HistorySetupCodes.IdentityNeeded"/>, true if a name is missing.</param>
/// <param name="NeedsEmail">For <see cref="HistorySetupCodes.IdentityNeeded"/>, true if an email is missing.</param>
public sealed record HistorySetupResult(
    HistorySetupStatus Status,
    string? Code,
    string? Problem,
    IReadOnlyList<string> Steps,
    string? Repository,
    bool RepositoryMade,
    string? GitIgnore,
    IReadOnlyList<string> IdentitySaved,
    HistoryEntry? Checkpoint,
    bool NeedsName = false,
    bool NeedsEmail = false);

/// <summary>The codes of a refused or incomplete turn-on.</summary>
public static class HistorySetupCodes
{
    /// <summary>A name or email given isn't usable: it's empty, too long, more than one line, or holds a control character or angle bracket.</summary>
    public const string BadArgument = "bad_argument";

    /// <summary>Git has no name or email for the den's repository, and none was given.</summary>
    public const string IdentityNeeded = "identity_needed";

    /// <summary>The den is inside a larger repository, which keeps its history already.</summary>
    public const string InRepository = "in_repository";

    /// <summary>The den's folder is already a repository with checkpoints.</summary>
    public const string HasRepository = "has_repository";

    /// <summary>Git isn't installed, or isn't on the PATH.</summary>
    public const string GitNotInstalled = "git_not_installed";

    /// <summary>Git refused the folder or failed at a step: the problem says what it said.</summary>
    public const string HistoryFailed = "history_failed";

    /// <summary>Writing <c>.gitignore</c> failed.</summary>
    public const string WriteFailed = "write_failed";
}

/// <summary>Rules for turning history on that every host shares.</summary>
public static class HistorySetup
{
    /// <summary>The first checkpoint's message.</summary>
    public const string FirstCheckpointMessage = "Start of history";

    /// <summary>The longest name or email it takes, in characters.</summary>
    public const int LongestIdentity = 200;

    /// <summary>
    /// Why <paramref name="value"/> can't be saved as git's <c>user.name</c>
    /// or <c>user.email</c> (<paramref name="what"/>: "name" or "email"), or
    /// null if it can. Any name will do, a pen name included, and any email,
    /// a placeholder included: it must be one line of at most
    /// <see cref="LongestIdentity"/> characters, with no control characters,
    /// and without <c>&lt;</c> or <c>&gt;</c>, which git can't record in one.
    /// </summary>
    public static string? IdentityProblem(string what, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Trim().Length == 0)
        {
            return $"The {what} can't be empty.";
        }

        if (value.Length > LongestIdentity)
        {
            return $"The {what} is at most {LongestIdentity} characters, not {value.Length}.";
        }

        if (value.Any(char.IsControl))
        {
            return $"The {what} must be one line, with no control characters.";
        }

        if (value.Contains('<') || value.Contains('>'))
        {
            return $"The {what} can't hold '<' or '>', which git can't record in one.";
        }

        return null;
    }
}
