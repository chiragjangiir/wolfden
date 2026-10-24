using Wolfden.Core.History;

namespace Wolfden.Git.Tests;

/// <summary>The tag freezing a book adds when the den has git history (design.md §5, #31).</summary>
public sealed class FreezeTagTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void TheTagIsNamedForTheUniverseAndTheBook()
    {
        Assert.Equal("book/vael/ember/final", FreezeTag.NameFor("vael", "ember"));
        Assert.Throws<ArgumentException>(() => FreezeTag.NameFor("vael", "../ember"));
        Assert.Throws<ArgumentException>(() => FreezeTag.NameFor("Vael", "ember"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void FreezingTagsTheLatestCheckpointOnceAndNeverMovesTheTag()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen("universes/vael/series/a/books/ember/book.md", "---\nstatus: final\n---\n");
        var frozen = repo.CommitAll("Freeze Ember");

        var first = FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.Tagged, first.Outcome);
        Assert.Equal(new HistoryTag("book/vael/ember/final", frozen), first.Tag);
        Assert.Equal("Book 'ember' of universe 'vael' is frozen by its status: final. Tagged its snapshot 'book/vael/ember/final'.", first.Message);

        repo.WriteDen("universes/vael/entities/note.md", "# Later\n");
        repo.CommitAll("later");
        var again = FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.AlreadyTagged, again.Outcome);
        Assert.Equal(new HistoryTag("book/vael/ember/final", frozen), again.Tag);
        Assert.Contains("already has a tag 'book/vael/ember/final', so no tag was added", again.Message, StringComparison.Ordinal);
        Assert.Equal(frozen, repo.Git("rev-parse", "book/vael/ember/final^{commit}").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void AnotherDenInTheRepositoryWithTheSameKeysDoesntMoveTheTag()
    {
        using var repo = TestRepo.Create("one");
        Directory.CreateDirectory(Path.Combine(repo.Root, "two"));
        repo.WriteRepo("two/den.yaml", "format: 1\n");
        var first = repo.CommitAll("two dens");
        Assert.Equal(FreezeTagOutcome.Tagged, FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token).Outcome);

        repo.WriteRepo("two/universes/vael/universe.md", "---\nname: Vael\n---\n");
        repo.CommitAll("second den grows");
        var other = FreezeTag.Add(GitHistory.ForDen(Path.Combine(repo.Root, "two"), TestRepo.Options, Token), "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.AlreadyTagged, other.Outcome);
        Assert.Equal(first, repo.Git("rev-parse", "book/vael/ember/final^{commit}").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void WithoutHistoryTheBookIsStillFrozenButNotTagged()
    {
        var result = FreezeTag.Add(NoHistory.Instance, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.NoHistory, result.Outcome);
        Assert.Null(result.Tag);
        Assert.StartsWith("Book 'ember' of universe 'vael' is frozen by its status: final. No tag was added: This den has no history", result.Message, StringComparison.Ordinal);
    }
}
