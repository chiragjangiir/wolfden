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
            Describe("log", None.Log(new HistoryLogQuery("universes/starfall/universe.md", 10))),
            Describe("diff", None.Diff(new HistoryDiffQuery("HEAD~1"))),
            Describe("checkpoint", None.Checkpoint(new CheckpointRequest("End of chapter 14 session", CoAuthors: ["Claude <noreply@anthropic.com>"]))),
            Describe("tag", None.Tag(new TagRequest("book/ember/final"))),
            Describe("tags", None.Tags("book/")),
        };

        Assert.All(results, r =>
        {
            Assert.False(r.Available, r.What);
            Assert.StartsWith("This den has no history backend, so ", r.Reason, StringComparison.Ordinal);
            Assert.EndsWith(NoHistory.Hint, r.Reason, StringComparison.Ordinal);
        });
        Assert.Equal(
            "This den has no history backend, so a checkpoint can't be recorded. Your edits are saved in the files as usual. " + NoHistory.Hint,
            results[2].Reason);
    }

    [Fact]
    public void TheValueOfAnUnavailableResultIsAProgrammingError()
    {
        var log = None.Log(new HistoryLogQuery());

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
