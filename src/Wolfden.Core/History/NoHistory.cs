namespace Wolfden.Core.History;

/// <summary>
/// The history provider of a den without a history backend (design.md
/// §6). The den loads, validates, and indexes as usual; every history
/// operation returns <see cref="HistoryResult{T}.Unavailable"/> with a
/// message that says why and how to get history, and none throws.
/// </summary>
public sealed class NoHistory : IHistoryProvider
{
    /// <summary>What every message ends with: how to get history.</summary>
    public const string Hint = "History comes from a backend such as git; keep the den in a git repository to get it (design.md §6).";

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
    public HistoryResult<IReadOnlyList<HistoryEntry>> Log(HistoryLogQuery query) =>
        HistoryResult<IReadOnlyList<HistoryEntry>>.Unavailable(Say("there's no history to show"));

    /// <inheritdoc />
    public HistoryResult<HistoryDiff> Diff(HistoryDiffQuery query) =>
        HistoryResult<HistoryDiff>.Unavailable(Say("there are no earlier versions to compare with"));

    /// <inheritdoc />
    public HistoryResult<HistoryEntry?> Checkpoint(CheckpointRequest request) =>
        HistoryResult<HistoryEntry?>.Unavailable(Say("a checkpoint can't be recorded. Your edits are saved in the files as usual"));

    /// <inheritdoc />
    public HistoryResult<HistoryTag> Tag(TagRequest request) =>
        HistoryResult<HistoryTag>.Unavailable(Say("a tag can't be made"));

    /// <inheritdoc />
    public HistoryResult<IReadOnlyList<HistoryTag>> Tags(string? prefix = null) =>
        HistoryResult<IReadOnlyList<HistoryTag>>.Unavailable(Say("there are no tags"));

    private static string Say(string what) => $"This den has no history backend, so {what}. {Hint}";
}
