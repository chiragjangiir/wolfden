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
        Assert.Equal("book/vael/ember/final-2", FreezeTag.NameFor("vael", "ember", 2));
        Assert.Throws<ArgumentException>(() => FreezeTag.NameFor("vael", "../ember"));
        Assert.Throws<ArgumentException>(() => FreezeTag.NameFor("Vael", "ember"));
        Assert.Throws<ArgumentOutOfRangeException>(() => FreezeTag.NameFor("vael", "ember", 0));
    }

    [Theory]
    [Trait("Scenario", "SC-HIST-7")]
    [InlineData("book/vael/ember/final", 1)]
    [InlineData("book/vael/ember/final-2", 2)]
    [InlineData("book/vael/ember/final-17", 17)]
    [InlineData("book/vael/ember/final-1", null)]
    [InlineData("book/vael/ember/final-02", null)]
    [InlineData("book/vael/ember/final-", null)]
    [InlineData("book/vael/ember/final-2x", null)]
    [InlineData("book/vael/ember/finale", null)]
    [InlineData("book/vael/ember-2/final", null)]
    [InlineData("book/vael/ember/final-9999999999", null)]
    public void OnlyTheBooksOwnFreezeTagsHaveANumber(string name, int? number)
    {
        Assert.Equal(number, FreezeTag.NumberOf(name, "vael", "ember"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void EachFreezeAddsTheNextNumberAndNeverMovesATag()
    {
        using var repo = TestRepo.Create();
        repo.WriteDen("universes/vael/series/a/books/ember/book.md", "---\nstatus: final\n---\n");
        var frozen = repo.CommitAll("Freeze Ember");

        var first = FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.Tagged, first.Outcome);
        Assert.Equal(new HistoryTag("book/vael/ember/final", frozen), first.Tag);
        Assert.Equal("Book 'ember' of universe 'vael' is frozen by its status: final. Tagged its snapshot 'book/vael/ember/final'.", first.Message);

        repo.WriteDen("universes/vael/entities/note.md", "# Later\n");
        var later = repo.CommitAll("later");
        var again = FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.Tagged, again.Outcome);
        Assert.Equal(new HistoryTag("book/vael/ember/final-2", later), again.Tag);
        Assert.Equal(frozen, repo.Git("rev-parse", "book/vael/ember/final^{commit}").Trim());
        Assert.Equal(
            ["book/vael/ember/final", "book/vael/ember/final-2"],
            FreezeTag.Existing(repo.History, "vael", "ember", Token).Select(t => t.Name));

        // One more than the highest, even when a number between is missing or a tag looks like one.
        repo.Git("tag", "book/vael/ember/final-7", frozen);
        repo.Git("tag", "book/vael/ember/final-08", frozen);
        Assert.Equal("book/vael/ember/final-8", FreezeTag.NextName(repo.History, "vael", "ember", Token));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void AnotherDenInTheRepositoryWithTheSameKeysCountsUpAndDoesntMoveTheTag()
    {
        using var repo = TestRepo.Create("one");
        Directory.CreateDirectory(Path.Combine(repo.Root, "two"));
        repo.WriteRepo("two/den.yaml", "format: 1\n");
        var first = repo.CommitAll("two dens");
        Assert.Equal(FreezeTagOutcome.Tagged, FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token).Outcome);

        repo.WriteRepo("two/universes/vael/universe.md", "---\nname: Vael\n---\n");
        var second = repo.CommitAll("second den grows");
        var other = FreezeTag.Add(GitHistory.ForDen(Path.Combine(repo.Root, "two"), TestRepo.Options, Token), "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.Tagged, other.Outcome);
        Assert.Equal(new HistoryTag("book/vael/ember/final-2", second), other.Tag);
        Assert.Equal(first, repo.Git("rev-parse", "book/vael/ember/final^{commit}").Trim());
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-7")]
    public void BeforeAnyCheckpointThereIsNothingToTag()
    {
        using var repo = TestRepo.Create();

        var result = FreezeTag.Add(repo.History, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.NothingCheckpointed, result.Outcome);
        Assert.Null(result.Tag);
        Assert.Contains("Nothing in the den has been checkpointed yet", result.Message, StringComparison.Ordinal);
        Assert.Equal("", repo.Git("tag", "--list"));
    }

    [Fact]
    [Trait("Scenario", "SC-HIST-1")]
    public void WithoutHistoryTheBookIsStillFrozenButNotTagged()
    {
        var result = FreezeTag.Add(NoHistory.Instance, "vael", "ember", cancellationToken: Token);

        Assert.Equal(FreezeTagOutcome.NoHistory, result.Outcome);
        Assert.Null(result.Tag);
        Assert.StartsWith("Book 'ember' of universe 'vael' is frozen by its status: final. No tag was added: This den has no history", result.Message, StringComparison.Ordinal);
        Assert.Empty(FreezeTag.Existing(NoHistory.Instance, "vael", "ember", Token));
    }
}
