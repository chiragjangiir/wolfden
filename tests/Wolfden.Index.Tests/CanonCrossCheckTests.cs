using System.Text.Json;
using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;

namespace Wolfden.Index.Tests;

/// <summary>
/// The index's canon-as-of view equals the reference one worked out from
/// Core (<see cref="CanonReference"/>) for every scope at every chapter,
/// every bare book, and the end of the story: on the Dracula sample, the
/// Starfall den, and seeded generated dens.
/// </summary>
public sealed class CanonCrossCheckTests
{
    /// <summary>The seeds of the generated dens. Each one is a different den, the same on every run.</summary>
    public static TheoryData<int> Seeds => [1, 2, 3, 5, 8, 13, 21, 34, 55, 89];

    [Fact]
    public void DraculaMatchesTheReference()
    {
        using var den = TempDen.Dracula();
        Assert.True(CrossCheck(den).Views > 50);
    }

    [Fact]
    public void StarfallMatchesTheReference()
    {
        using var den = TempDen.Starfall();
        Assert.True(CrossCheck(den).Views > 50);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedDensMatchTheReference(int seed)
    {
        var (files, count) = DenGenerator.Generate(seed);
        using var den = TempDen.FromFiles(files);
        var catalog = EntityCatalog.Load(den.Root);
        var errors = catalog.Structure.Diagnostics.Concat(catalog.Diagnostics)
            .Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Path}:{d.Diagnostic.Line} {d.Diagnostic.RuleId} {d.Diagnostic.Message}")
            .ToList();

        // The generator writes files that load, so the check covers all of them.
        Assert.Empty(errors);
        Assert.Equal(count, catalog.Entities.Count);
        CrossCheck(den);
    }

    [Fact]
    public void GeneratedDensExerciseTheRules()
    {
        // The generated dens are only worth checking against if their views
        // hold what the rules decide between.
        var fields = new List<CanonFieldValue>();
        var sections = new List<CanonSection>();
        var statuses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in Seeds)
        {
            var (files, _) = DenGenerator.Generate(seed);
            using var den = TempDen.FromFiles(files);
            var (_, views) = CrossCheck(den);
            fields.AddRange(views.SelectMany(v => v.Entities).SelectMany(e => e.Fields));
            sections.AddRange(views.SelectMany(v => v.Entities).SelectMany(e => e.Sections));
            statuses.UnionWith(EntityCatalog.Load(den.Root).Entities.Select(e => e.Status.ToKey()));
        }

        Assert.Equal(["default", "entry", "plain", "temporal"], fields.Select(f => f.Kind).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(fields, f => f.Kind == "temporal" && f.Until is not null);
        Assert.Contains(fields, f => f.Kind == "temporal" && f.EntryIndex > 0);
        Assert.Contains(fields, f => f.Pinned);
        Assert.Contains(fields, f => f.Status == "draft");
        Assert.Contains(sections, s => s.Level == 3);
        Assert.Contains(sections, s => s.Until is not null);
        Assert.Contains(sections, s => s.Pinned);
        Assert.Contains(sections, s => s.Name == "Payoff");
        Assert.Superset(new HashSet<string> { "idea", "retconned", "candidate", "draft", "canon" }, statuses);
    }

    /// <summary>
    /// Checks the index against the reference for every scope and position
    /// of every universe of the den, and that nothing in a view starts
    /// after its position. Returns the views checked.
    /// </summary>
    internal static (int Views, List<CanonView> All) CrossCheck(TempDen den)
    {
        using var index = DenIndex.Open(den.Root);
        var catalog = EntityCatalog.Load(den.Root);
        var all = new List<CanonView>();
        foreach (var universe in catalog.Structure.Universes)
        {
            foreach (var scope in Scopes(universe))
            {
                foreach (var position in Positions(universe))
                {
                    var actual = index.CanonAsOf(scope, position);
                    var expected = CanonReference.Compute(catalog, scope, position);
                    AssertSame(expected, actual);
                    AssertSame(expected.Pinned(), actual.Pinned());
                    NothingStartsAfter(universe, actual);
                    all.Add(actual);
                }
            }
        }

        return (all.Count, all);
    }

    /// <summary>Every scope of a universe: itself, its series, and its books.</summary>
    internal static IEnumerable<CanonScope> Scopes(Universe universe) =>
        new[] { CanonScope.ForUniverse(universe.Key) }
            .Concat(universe.Series.Select(s => CanonScope.ForSeries(universe.Key, s.Key)))
            .Concat(universe.Books.Select(b => CanonScope.ForBook(universe.Key, b.Key)));

    /// <summary>Every position of a universe: each bare book, each chapter, and the end of the story (null).</summary>
    internal static IEnumerable<string?> Positions(Universe universe) =>
        universe.Books
            .SelectMany(b => b.Chapters.Select(c => $"{b.Key}:{c.Key}").Prepend(b.Key))
            .Append(null);

    /// <summary>
    /// F-VIEW-1: nothing in a view enters canon after its position. Every
    /// written <c>from</c> in it is at or before the position, and every
    /// written <c>until</c> after it (at the end of the story, a fact with
    /// an <c>until</c> has ended).
    /// </summary>
    internal static void NothingStartsAfter(Universe universe, CanonView view)
    {
        var p = view.Position is { } text ? Point(universe, text)!.Value : universe.EndOfStory;
        foreach (var e in view.Entities)
        {
            Check(e.From, null, $"{e.Path}");
            foreach (var f in e.Fields)
            {
                Check(f.From, f.Until, $"{e.Path} field {f.Name}");
            }

            foreach (var s in e.Sections)
            {
                Check(s.From, s.Until, $"{e.Path} section {s.Name} (line {s.Line})");
            }
        }

        void Check(string? from, string? until, string what)
        {
            if (from is not null)
            {
                var start = Point(universe, from);
                Assert.True(start is { } f && f <= p, $"{what} starts at '{from}', after the view's position '{view.Position}'.");
            }

            if (until is not null)
            {
                var end = Point(universe, until);
                Assert.True(!p.IsEndOfStory && end is { } u && p < u, $"{what} ends at '{until}', not after the view's position '{view.Position}'.");
            }
        }
    }

    /// <summary>Asserts two views are the same, naming the first entity where they differ.</summary>
    internal static void AssertSame(CanonView expected, CanonView actual)
    {
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.Position, actual.Position);
        var want = expected.Entities.Select(e => JsonSerializer.Serialize(e)).ToList();
        var got = actual.Entities.Select(e => JsonSerializer.Serialize(e)).ToList();
        for (var i = 0; i < Math.Max(want.Count, got.Count); i++)
        {
            var w = i < want.Count ? want[i] : "(none)";
            var g = i < got.Count ? got[i] : "(none)";
            if (w != g)
            {
                Assert.Fail($"The views of {expected.Scope} at '{expected.Position ?? "the end of the story"}' differ at entity {i}.\nReference: {w}\nIndex:     {g}");
            }
        }
    }

    private static StoryPoint? Point(Universe universe, string position) =>
        StoryPosition.TryParse(position, out var parsed, out _) ? universe.Resolve(parsed).Resolved?.Point : null;
}
