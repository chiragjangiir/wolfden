using Wolfden.Core.Checks;
using Wolfden.Core.History;

namespace Wolfden.Core.Tests;

/// <summary>
/// The history provider's contract (#121, design.md §6), through the
/// provider of a den without a history backend (<see cref="NoHistory"/>):
/// every operation says history isn't available, in words for the writer,
/// and none throws. The git provider (#19) is tested in its own project.
/// </summary>
public sealed class HistoryProviderTests
{
    private static readonly IHistoryProvider None = NoHistory.Instance;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void ADenWithoutABackendHasNoHistoryCapabilities()
    {
        Assert.Equal("none", None.Name);
        Assert.Equal(HistoryCapabilities.None, None.Capabilities);
        Assert.False(None.Capabilities.HasFlag(HistoryCapabilities.Tag));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void EveryOperationSaysHistoryIsntAvailableWithoutThrowing()
    {
        var results = new (string What, bool Available, string? Reason)[]
        {
            Describe("log", None.Log(new HistoryLogQuery("universes/starfall/universe.md", 10), Token)),
            Describe("diff", None.Diff(new HistoryDiffQuery(), Token)),
            Describe("checkpoint", None.Checkpoint(new CheckpointRequest("End of chapter 14 session", CoAuthors: ["Claude <noreply@anthropic.com>"]), Token)),
            Describe("tag", None.Tag(new TagRequest("ember-final"), Token)),
            Describe("tags", None.Tags("ember", Token)),
        };

        Assert.All(results, r =>
        {
            Assert.False(r.Available, r.What);
            Assert.StartsWith("This den has no history, so ", r.Reason, StringComparison.Ordinal);
            Assert.EndsWith(NoHistory.Hint, r.Reason, StringComparison.Ordinal);
        });
        Assert.Equal(
            "This den has no history, so a note's history isn't available. To keep history, run 'wolfden init --git' in the den's folder, or use Turn on history in the web UI's checkpoint dialog. Wolfden keeps it with git, which shouldn't be in a synced folder (Dropbox, OneDrive, iCloud); use a private remote for backup.",
            results[0].Reason);
        Assert.Equal(
            "This den has no history, so a checkpoint isn't available. Your edits are still saved in the files. " + NoHistory.Hint,
            results[2].Reason);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void TheMessagesSendTheWriterNowhereTheyCantGo()
    {
        // No design docs, issue numbers, or commands that don't exist: the one command named is 'wolfden init --git' (#195).
        var reasons = new[]
        {
            None.Log(new HistoryLogQuery(), Token).Reason!,
            None.Diff(new HistoryDiffQuery(), Token).Reason!,
            None.Checkpoint(new CheckpointRequest("x"), Token).Reason!,
            None.Tag(new TagRequest("x"), Token).Reason!,
            None.Tags(cancellationToken: Token).Reason!,
        };

        Assert.All(reasons, r =>
        {
            Assert.DoesNotContain(".md", r, StringComparison.Ordinal);
            Assert.DoesNotContain("#", r, StringComparison.Ordinal);
            Assert.DoesNotContain("wolfden ", r.Replace("'wolfden init --git'", "", StringComparison.Ordinal), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheValueOfAnUnavailableResultIsAProgrammingError()
    {
        var log = None.Log(new HistoryLogQuery(), Token);

        var e = Assert.Throws<InvalidOperationException>(() => log.Value);

        Assert.Equal(log.Reason, e.Message);
    }

    [Fact]
    public void ADoneResultHoldsItsValue()
    {
        var entry = new HistoryEntry("abc123", DateTimeOffset.UnixEpoch, "Gavin", "Checkpoint", [new HistoryFileChange(HistoryChangeKind.Added, "den.yaml")]);

        var done = HistoryResult<HistoryEntry?>.Done(entry);
        var nothing = HistoryResult<HistoryEntry?>.Done(null);

        Assert.True(done.IsAvailable);
        Assert.Null(done.Reason);
        Assert.Same(entry, done.Value);
        Assert.True(nothing.IsAvailable);
        Assert.Null(nothing.Value);
    }

    [Fact]
    public void AnUnavailableResultMustSayWhy()
    {
        Assert.Throws<ArgumentException>(() => HistoryResult<HistoryTag>.Unavailable(" "));
    }

    [Fact]
    public void ADiffWithoutRevisionsIsSinceTheLatestCheckpointAndALogStartsAtTheNewest()
    {
        var diff = new HistoryDiffQuery();
        var log = new HistoryLogQuery();

        Assert.Null(diff.From);
        Assert.Null(diff.To);
        Assert.Null(log.Before);
        Assert.Null(log.Limit);
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void ADenWithoutHistoryStillLoadsAndChecks()
    {
        // History is a capability, not part of the format (design.md §6):
        // the Starfall fixture isn't in a repository of its own.
        var check = DenCheck.Run(MemoryDenSource.FromDirectory(MemoryDenSource.StarfallRoot));

        Assert.True(check.IsLoaded);
        Assert.Equal(HistoryCapabilities.None, NoHistory.Instance.Capabilities);
    }

    [Fact]
    public void AllIsEveryCapability()
    {
        Assert.Equal(
            HistoryCapabilities.Log | HistoryCapabilities.Diff | HistoryCapabilities.Checkpoint | HistoryCapabilities.Tag,
            HistoryCapabilities.All);
    }

    private static (string, bool, string?) Describe<T>(string what, HistoryResult<T> result) => (what, result.IsAvailable, result.Reason);
}
