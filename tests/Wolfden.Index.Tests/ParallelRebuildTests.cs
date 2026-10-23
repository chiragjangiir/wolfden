namespace Wolfden.Index.Tests;

/// <summary>Several callers that find the same stale index at once rebuild it once (#20 review).</summary>
public sealed class ParallelRebuildTests
{
    [Fact]
    public async Task ParallelCallsOnAMissingIndexRebuildItOnce()
    {
        using var den = TempDen.Dracula();
        var root = Path.GetFullPath(den.Root);
        var cancel = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => IndexBuilder.EnsureUpToDate(root, TimeSpan.Zero), cancel)));

        Assert.Equal(1, IndexBuilder.RebuildCounts[root]);
        Assert.Equal(IndexStatus.Current, IndexBuilder.CheckFiles(root));
    }

    [Fact]
    public async Task ParallelCallsOnAStaleIndexRebuildItOnce()
    {
        using var den = TempDen.Dracula();
        var root = Path.GetFullPath(den.Root);
        var cancel = TestContext.Current.CancellationToken;
        IndexBuilder.Rebuild(root);
        den.Write("universes/dracula/entities/locations/new-place.md", "---\ntype: location\nname: New Place\n---\n\nSomewhere.\n");
        var before = IndexBuilder.RebuildCounts[root];

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => IndexBuilder.EnsureUpToDate(root, TimeSpan.Zero), cancel)));

        Assert.Equal(before + 1, IndexBuilder.RebuildCounts[root]);
    }
}
