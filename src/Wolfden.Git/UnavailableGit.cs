using Wolfden.Core.History;

namespace Wolfden.Git;

/// <summary>
/// The history provider of a den whose git history can't be used: git
/// isn't installed, or git refuses the repository the den is in (for
/// example because of <c>safe.directory</c>). Like <see cref="NoHistory"/>,
/// every operation says what isn't available and why, and none throws.
/// </summary>
internal sealed class UnavailableGit : IHistoryProvider
{
    private readonly string _cause;
    private readonly string _fix;

    /// <param name="cause">Why history can't be used, as the start of a sentence, such as "Git isn't installed".</param>
    /// <param name="fix">What the writer can do about it, as a sentence.</param>
    public UnavailableGit(string cause, string fix)
    {
        _cause = cause;
        _fix = fix;
    }

    public string Name => "git";

    public HistoryCapabilities Capabilities => HistoryCapabilities.None;

    public HistoryResult<IReadOnlyList<HistoryEntry>> Log(HistoryLogQuery query, CancellationToken cancellationToken = default) =>
        HistoryResult<IReadOnlyList<HistoryEntry>>.Unavailable(Say("a note's history"));

    public HistoryResult<HistoryDiff> Diff(HistoryDiffQuery query, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryDiff>.Unavailable(Say("comparing with an earlier version"));

    public HistoryResult<HistoryDiff> DiffCheckpoint(string revision, string? path = null, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryDiff>.Unavailable(Say("comparing with an earlier version"));

    public HistoryResult<HistoryEntry?> Checkpoint(CheckpointRequest request, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryEntry?>.Unavailable(Say("a checkpoint", "Your edits are still saved in the files."));

    public HistoryResult<HistoryTag> Tag(TagRequest request, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryTag>.Unavailable(Say("a tag"));

    public HistoryResult<IReadOnlyList<HistoryTag>> Tags(string? prefix = null, CancellationToken cancellationToken = default) =>
        HistoryResult<IReadOnlyList<HistoryTag>>.Unavailable(Say("the list of tags"));

    private string Say(string what, string? more = null) =>
        more is null ? $"{_cause}, so {what} isn't available. {_fix}" : $"{_cause}, so {what} isn't available. {more} {_fix}";
}
