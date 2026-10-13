using System.Diagnostics;
using System.Text;

namespace Wolfden.Index.Tests;

/// <summary>
/// A smoke test that a rebuild of a large den stays fast. The bound is
/// generous, to catch a return to per-row EF inserts or quadratic time, not
/// to measure speed: in development this den rebuilds in a few seconds.
/// </summary>
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    [Fact]
    public void ALargeDenRebuildsQuickly()
    {
        // 4,000 entities, each with 12 sections, 8 links, and 3 field values.
        var files = new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\n",
            ["universes/big/universe.md"] = "---\nname: Big\norder: [saga]\n---\n",
            ["universes/big/series/saga/series.md"] = "---\nname: Saga\nbooks: [one, two]\n---\n",
            ["universes/big/series/saga/books/one/book.md"] = "---\nname: One\nchapters: [1, 2, 3, 4, 5]\n---\n",
            ["universes/big/series/saga/books/two/book.md"] = "---\nname: Two\nchapters: [1, 2, 3, 4, 5]\n---\n",
        };
        const int count = 4_000;
        for (var i = 0; i < count; i++)
        {
            var body = new StringBuilder($"\nPerson number {i}, who knows [[person-{(i + 1) % count}]].\n");
            for (var s = 0; s < 12; s++)
            {
                body.Append($"\n## Part {s}\n<!-- wolfden: from=one:{s % 5 + 1} -->\nText about part {s} and [[person-{(i + s) % count}]].\n");
            }

            files[$"universes/big/series/saga/entities/characters/person-{i}.md"] =
                $"---\ntype: character\nname: Person {i}\nstatus: canon\nfields:\n  rank:\n    - {{ value: A, from: \"one:1\" }}\n    - {{ value: B, from: \"two:1\" }}\n  born: \"{i}\"\n---\n{body}";
        }

        using var den = TempDen.FromFiles(files);
        // Read the new files once untimed: a virus scanner's first look at
        // thousands of new files can take far longer than the rebuild.
        _ = Wolfden.Core.Links.LinkIndex.Load(den.Root);
        var watch = Stopwatch.StartNew();
        IndexBuilder.Rebuild(den.Root);
        output.WriteLine($"Rebuilt {count} entities in {watch.Elapsed.TotalSeconds:F1} s.");

        using var index = DenIndex.Open(den.Root);
        Assert.Equal(count, index.Context.Entities.Count());
        Assert.Equal(count * 12, index.Context.Sections.Count());
        Assert.Equal(count * 13, index.Context.Links.Count());
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed}.");
    }
}
