namespace Wolfden.Core.History;

/// <summary>
/// The history provider of a den without a history backend (design.md
/// §6). The den loads, validates, and indexes as usual; every history
/// operation returns <see cref="HistoryResult{T}.Unavailable"/> with a
/// message that says what isn't available and how a writer gets history,
/// and none throws.
/// </summary>
public sealed class NoHistory : IHistoryProvider
{
    /// <summary>What every message ends with: how to get history.</summary>
    public const string Hint =
        "Wolfden can keep history with git if the den is in a git repository that isn't in a synced folder (Dropbox, OneDrive, iCloud); use a private remote for backup.";

    private NoHistory()
    {
    }

    /// <summary>The one instance; it holds nothing.</summary>
    public static NoHistory Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "none";

    /// <inheritdoc />
    public HistoryCapabilities Capabilities => HistoryCapabilities.None;

    /// <inheritdoc />
    public HistoryResult<IReadOnlyList<HistoryEntry>> Log(HistoryLogQuery query, CancellationToken cancellationToken = default) =>
        HistoryResult<IReadOnlyList<HistoryEntry>>.Unavailable(Say("a note's history"));

    /// <inheritdoc />
    public HistoryResult<HistoryDiff> Diff(HistoryDiffQuery query, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryDiff>.Unavailable(Say("comparing with an earlier version"));

    /// <inheritdoc />
    public HistoryResult<HistoryDiff> DiffCheckpoint(string revision, string? path = null, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryDiff>.Unavailable(Say("comparing with an earlier version"));

    /// <inheritdoc />
    public HistoryResult<HistoryEntry?> Checkpoint(CheckpointRequest request, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryEntry?>.Unavailable(Say("a checkpoint", "Your edits are still saved in the files."));

    /// <inheritdoc />
    public HistoryResult<HistoryTag> Tag(TagRequest request, CancellationToken cancellationToken = default) =>
        HistoryResult<HistoryTag>.Unavailable(Say("a tag"));

    /// <inheritdoc />
    public HistoryResult<IReadOnlyList<HistoryTag>> Tags(string? prefix = null, CancellationToken cancellationToken = default) =>
        HistoryResult<IReadOnlyList<HistoryTag>>.Unavailable(Say("the list of tags"));

    private static string Say(string what, string? more = null) =>
        more is null
            ? $"This den has no history, so {what} isn't available. {Hint}"
            : $"This den has no history, so {what} isn't available. {more} {Hint}";
}
