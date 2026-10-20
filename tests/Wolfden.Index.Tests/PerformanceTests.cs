using System.Diagnostics;
using System.Text;

namespace Wolfden.Index.Tests;

/// <summary>
/// Smoke tests that a rebuild of a large den, and an update after one edit,
/// stay fast. The bounds are generous, to catch a return to per-row EF
/// inserts or quadratic time, not to measure speed: in development this den
/// rebuilds in a few seconds.
/// </summary>
public sealed class PerformanceTests(ITestOutputHelper output)
{
    private const int Count = 4_000;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan UpdateBound = TimeSpan.FromSeconds(10);

    [Fact]
    public void ALargeDenRebuildsQuickly()
    {
        using var den = TempDen.FromFiles(LargeDen());
        // Read the new files once untimed: a virus scanner's first look at
        // thousands of new files can take far longer than the rebuild.
        _ = Wolfden.Core.Links.LinkIndex.Load(den.Root);
        var watch = Stopwatch.StartNew();
        IndexBuilder.Rebuild(den.Root);
        output.WriteLine($"Rebuilt {Count} entities in {watch.Elapsed.TotalSeconds:F1} s.");

        using var index = DenIndex.Open(den.Root);
        Assert.Equal(Count, index.Context.Entities.Count());
        Assert.Equal(Count * 12, index.Context.Sections.Count());
        Assert.Equal(Count * 13, index.Context.Links.Count());
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed}.");
    }

    /// <summary>
    /// The file watcher's update after one note changes (#14) reads only
    /// that note and writes only the rows that changed. The bound is
    /// generous; in development it's well under a second, against several
    /// seconds for a rebuild.
    /// </summary>
    [Fact]
    [Trait("Scenario", "SC-WATCH-1")]
    public void OneEditOnALargeDenIsUpdatedQuickly()
    {
        using var den = TempDen.FromFiles(LargeDen());
        _ = Wolfden.Core.Links.LinkIndex.Load(den.Root);
        var updater = new IndexUpdater(den.Root);
        var watch = Stopwatch.StartNew();
        updater.Rebuild("start");
        var rebuild = watch.Elapsed;

        // Rename one person: their own rows change, and so do the links that named them.
        const string path = "universes/big/series/saga/entities/characters/person-7.md";
        var full = Path.Combine(den.Root, path.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full, File.ReadAllText(full).Replace("name: Person 7", "name: Person Seven", StringComparison.Ordinal));
        var batch = new ChangeBatch(new HashSet<string>([path]), new HashSet<string>(), false);
        watch.Restart();
        var renamed = updater.Apply(batch, out _);
        var rename = watch.Elapsed;

        // Add a sentence with a link: only this note's rows change.
        File.AppendAllText(full, "\nOne more sentence, with a link to [[person-9]].\n");
        watch.Restart();
        var appended = updater.Apply(batch, out _);
        var append = watch.Elapsed;

        output.WriteLine(
            $"{Count} entities: rebuild {rebuild.TotalMilliseconds:F0} ms; one note renamed {rename.TotalMilliseconds:F0} ms ({renamed!.Rows} rows); " +
            $"one sentence added {append.TotalMilliseconds:F0} ms ({appended!.Rows} rows).");
        Assert.Equal(IndexUpdateKind.Updated, renamed.Kind);
        Assert.Equal(IndexUpdateKind.Updated, appended.Kind);
        Assert.True(append < UpdateBound, $"Took {append}.");
        using var index = DenIndex.Open(den.Root);
        Assert.Equal("Person Seven", index.FindEntity(path)!.Name);
        Assert.Equal(Count * 13 + 1, index.Context.Links.Count());
    }

    /// <summary>4,000 entities, each with 12 sections, 8 links, and 3 field values.</summary>
    private static Dictionary<string, string> LargeDen()
    {
        var files = new Dictionary<string, string>
        {
            ["den.yaml"] = "format: 1\n",
            ["universes/big/universe.md"] = "---\nname: Big\norder: [saga]\n---\n",
            ["universes/big/series/saga/series.md"] = "---\nname: Saga\nbooks: [one, two]\n---\n",
            ["universes/big/series/saga/books/one/book.md"] = "---\nname: One\nchapters: [1, 2, 3, 4, 5]\n---\n",
            ["universes/big/series/saga/books/two/book.md"] = "---\nname: Two\nchapters: [1, 2, 3, 4, 5]\n---\n",
        };
        for (var i = 0; i < Count; i++)
        {
            var body = new StringBuilder($"\nPerson number {i}, who knows [[person-{(i + 1) % Count}]].\n");
            for (var s = 0; s < 12; s++)
            {
                body.Append($"\n## Part {s}\n<!-- wolfden: from=one:{s % 5 + 1} -->\nText about part {s} and [[person-{(i + s) % Count}]].\n");
            }

            files[$"universes/big/series/saga/entities/characters/person-{i}.md"] =
                $"---\ntype: character\nname: Person {i}\nstatus: canon\nfields:\n  rank:\n    - {{ value: A, from: \"one:1\" }}\n    - {{ value: B, from: \"two:1\" }}\n  born: \"{i}\"\n---\n{body}";
        }

        return files;
    }
}
