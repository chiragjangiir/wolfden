using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Wolfden.Core.Structure;
using Wolfden.Index;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden query [&lt;universe&gt;] [--at &lt;position&gt;] ...</c>: the
/// canon-as-of view of a universe (format.md §9.4, F-VIEW-1), from the
/// index. What a view shows is decided by story position, not by folder;
/// <c>--series</c> or <c>--book</c> narrows it to that work's scopes, and
/// <c>--type</c> and <c>--entity</c> pick entities out of it.
/// </summary>
/// <remarks>
/// <para>
/// It brings the index up to date first, rebuilding it if the files
/// changed since it was built (<see cref="IndexBuilder.EnsureUpToDate(string)"/>),
/// so the answer always matches the files. While <c>wolfden index --watch</c>
/// runs, it first waits up to about a second for the watcher to catch up.
/// </para>
/// <para>
/// Exit codes: 0 when the view is shown, even if it's empty, and 2 when
/// the command line is wrong, there's no den, <c>den.yaml</c> doesn't
/// load, the universe, filter, type, or entity doesn't exist, the position
/// doesn't resolve, or the index couldn't be read or rebuilt.
/// </para>
/// </remarks>
internal static partial class QueryCommand
{
    internal static readonly CommandSpec Spec = new(
        "query",
        "usage: wolfden query [<universe> | --universe <key>] [--at <position>] [--series <key> | --book <key>] [--type <type>]... [--entity <name>] [--pinned] [--brief] [--den <den>] [--json]",
        """
        Shows canon as of a story position: every entity the reader has met by
        then, at any scope of the universe, with the field values and sections
        in effect there. Ideas, retconned material, and bookkeeping sections are
        left out, and draft material is marked draft.

          <universe>, --universe <key>
                              the universe's key; it can be left out when the
                              den has only one universe, or when --at names a
                              book only one universe has
          --at <position>     a story position, 'book:chapter' or a bare 'book'
                              for the start of that book; by default, the end of
                              the story (all canon so far)
          --series <key>      only entities filed in this series, its books, or
                              the universe
          --book <key>        only entities filed in this book, its series, or
                              the universe
          --type <type>       only entities of this type, such as 'character';
                              give it more than once for several types
          --entity <name>     only this entity, named as a link would name it:
                              by name, alias, file name, or id, among the
                              entities the reader has met by then
          --pinned            only pinned facts: pinned canon (F-VIEW-2)
          --brief             one line per entity: no fields or sections
          --den <den>         the den's folder; by default, the current folder or
                              the nearest folder above it with a den.yaml
          --json              print one JSON document instead of text

        It reads the index, and rebuilds it first if the files have changed.
        Exits 0 when the view is shown, even if it's empty, and 2 if it
        couldn't be.
        """,
        ["--json", "--pinned", "--brief"],
        ["--den", "--universe", "--at", "--series", "--book", "--type", "--entity"],
        ["--type"],
        1,
        "give at most one universe.");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        if (line.Value("--series") is not null && line.Value("--book") is not null)
        {
            error.WriteLine("wolfden query: give '--series' or '--book', not both.");
            error.WriteLine(Spec.Usage);
            return CliApp.ExitUsage;
        }

        if (line.Arguments.Count > 0 && line.Value("--universe") is not null)
        {
            error.WriteLine("wolfden query: give the universe once, as an argument or with '--universe', not both.");
            error.WriteLine(Spec.Usage);
            return CliApp.ExitUsage;
        }

        if (DenLocation.Resolve(Spec.Name, line.Value("--den"), error) is not { } root)
        {
            return CliApp.ExitUsage;
        }

        var filter = line.Value("--series") is { } series ? CanonFilter.ForSeries(series)
            : line.Value("--book") is { } book ? CanonFilter.ForBook(book)
            : null;
        try
        {
            IndexBuilder.EnsureUpToDate(root);
            using var index = DenIndex.Open(root);
            var result = Query(index, root, line, filter, out var problem);
            if (result is null)
            {
                error.WriteLine($"wolfden query: {problem}");
                return CliApp.ExitUsage;
            }

            if (line.Has("--json"))
            {
                CliJson.Write(output, QueryReport.From(result, line.Has("--pinned")));
            }
            else
            {
                WriteText(result, line.Has("--pinned"), line.Has("--brief"), output);
            }

            return CliApp.ExitOk;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine($"wolfden query: couldn't read or rebuild the index of the den at '{root}': {e.Message}");
            return CliApp.ExitUsage;
        }
    }

    /// <summary>The view the command line asks for, or null with <paramref name="problem"/> saying why there's none.</summary>
    private static CanonView? Query(DenIndex index, string root, CommandLine line, CanonFilter? filter, out string problem)
    {
        var db = index.Context;
        problem = "";
        if (!db.Dens.Single().IsLoaded)
        {
            problem = "den.yaml doesn't load, so there's nothing to query. Run 'wolfden check' to see why.";
            return null;
        }

        // The universe named, the den's only one, or the one --at's book
        // belongs to (maintainer decision, 2026-10-23). A canon view is of one
        // universe, so '*' (every universe) isn't a choice here.
        var named = line.Arguments.Count > 0 ? line.Arguments[0] : line.Value("--universe");
        if (named == "*")
        {
            problem = "a canon view is of one universe, so '*' (every universe) doesn't fit here; name one.";
            return null;
        }

        string universe;
        try
        {
            universe = UniverseChoice.Choose(db, named, line.Value("--at"));
        }
        catch (UniverseChoiceException e)
        {
            problem = e.Problem switch
            {
                UniverseChoiceProblem.NoUniverse => "the den has no universes yet, so there's nothing to query.",
                UniverseChoiceProblem.UniverseRequired =>
                    $"{e.Message} Name it with '--universe <key>', or give '--at' a position whose book only one universe has.",
                _ => e.Message,
            };
            return null;
        }

        CanonView view;
        try
        {
            view = index.CanonAsOf(universe, line.Value("--at"), filter);
        }
        catch (CanonViewException e)
        {
            problem = e.Message;
            return null;
        }

        // The view before --pinned narrows it: --entity matches among what the
        // reader has met (of the --type asked for, below).
        var met = view.Entities.ToList();
        if (line.Has("--pinned"))
        {
            view = view.Pinned();
        }

        var universeId = db.Containers.Single(c => c.Kind == "universe" && c.Key == universe).Id;
        var types = line.Values("--type");
        if (types.Count > 0)
        {
            // Defined types (built-in and den-local packs), plus any type an
            // entity uses without a definition (F-ENT-2), so it can still be picked out.
            var known = DenStructure.Load(root).Types.Types.Select(t => t.Key)
                .Concat(db.Entities.Where(e => e.UniverseId == universeId).Select(e => e.Type).Distinct())
                .ToHashSet(StringComparer.Ordinal);
            if (types.FirstOrDefault(t => !known.Contains(t)) is { } unknown)
            {
                problem = $"there's no type '{unknown}': no enabled pack defines it, and no entity in universe '{universe}' uses it.";
                return null;
            }

            view = view with { Entities = view.Entities.Where(e => types.Contains(e.Type)).ToList() };
            met = met.Where(e => types.Contains(e.Type)).ToList();
        }

        if (line.Value("--entity") is { } name)
        {
            // Matched among the entities in the view, of the types asked for,
            // so one the reader meets later, or one of another type, can't
            // shadow or tie with the one wanted. Only if none matches is the
            // whole universe asked, to tell "not met yet" (an empty view) from
            // "no such entity" (an error).
            var among = met.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            var paths = index.MatchEntities(universeId, name, among).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            if (paths.Count == 0 && index.MatchEntities(universeId, name).Count == 0)
            {
                problem = $"universe '{universe}' has no entity '{name}' (matched as a link is: by id, file name, name, or alias).";
                return null;
            }

            view = view with { Entities = view.Entities.Where(e => paths.Contains(e.Path)).ToList() };
        }

        return view;
    }

    private static void WriteText(CanonView view, bool pinned, bool brief, TextWriter output)
    {
        var at = view.Position is null ? "at the end of the story" : $"as of {view.Position}";
        var filter = view.Filter is null ? "" : $", filtered to {view.Filter}";
        var what = pinned ? "Pinned canon" : "Canon";
        output.WriteLine($"{what} of universe '{view.Universe}' {at}{filter}: {CheckCommand.Count(view.Entities.Count, "entity", "entities")}.");
        foreach (var entity in view.Entities)
        {
            if (brief)
            {
                output.WriteLine($"- {entity.Name} ({Describe(entity)}): {entity.Path}");
                continue;
            }

            output.WriteLine();
            output.WriteLine($"# {entity.Name}");
            output.WriteLine($"{Describe(entity)}: {entity.Path}");
            if (entity.Fields.Count > 0)
            {
                output.WriteLine();
                foreach (var field in entity.Fields)
                {
                    output.WriteLine($"- {field.Name}: {FieldText(field.ValueJson)}{Notes(field)}");
                }
            }

            var lead = Lead(entity);
            if (lead.Length > 0)
            {
                output.WriteLine();
                WriteBlock(output, lead);
            }

            foreach (var section in entity.Sections)
            {
                output.WriteLine();
                output.WriteLine($"{new string('#', section.Level)} {section.Name}{Notes(section)}");
                var body = Trim(section.Body);
                if (body.Length > 0)
                {
                    WriteBlock(output, body);
                }
            }
        }
    }

    private static string Describe(CanonEntity e) =>
        $"{e.Type}, {e.Status}, filed in {e.ScopeKind} '{e.ScopeKey}'{(e.From is null ? "" : $", from {e.From}")}";

    private static string Notes(CanonFieldValue f) => f.IsDefault
        ? " (default)"
        : Notes(f.From, f.Until, f.Pinned, f.Status == "draft");

    private static string Notes(CanonSection s) => Notes(s.From, s.Until, s.Pinned, s.Status == "draft");

    private static string Notes(string? from, string? until, bool pinned, bool draft)
    {
        var notes = new List<string>();
        if (from is not null)
        {
            notes.Add($"from {from}");
        }

        if (until is not null)
        {
            notes.Add($"until {until}");
        }

        if (pinned)
        {
            notes.Add("pinned");
        }

        if (draft)
        {
            notes.Add("draft");
        }

        return notes.Count == 0 ? "" : $" ({string.Join(", ", notes)})";
    }

    /// <summary>A field value for reading: a scalar as written, a list joined with commas, and anything else as compact JSON.</summary>
    internal static string FieldText(string valueJson)
    {
        using var json = JsonDocument.Parse(valueJson);
        return Text(json.RootElement);

        static string Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString()!,
            JsonValueKind.Null => "",
            JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Text)),
            JsonValueKind.Object => JsonSerializer.Serialize(e, CompactJson),
            _ => e.GetRawText(),
        };
    }

    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = CliJson.Options.Encoder };

    /// <summary>The lead without its level-1 title, which the entity's heading already shows.</summary>
    private static string Lead(CanonEntity entity)
    {
        var lead = entity.Lead.ReplaceLineEndings("\n");
        if (entity.Title is not null && TitleLine().Match(lead) is { Success: true } m)
        {
            lead = lead.Remove(m.Index, m.Length);
        }

        return Trim(lead);
    }

    /// <summary>The text with LF line breaks and without blank lines or trailing spaces at either end.</summary>
    private static string Trim(string text) => text.ReplaceLineEndings("\n").Trim('\n').TrimEnd();

    /// <summary>Writes text line by line, so the lines end the way the writer's do.</summary>
    private static void WriteBlock(TextWriter output, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            output.WriteLine(line);
        }
    }

    [GeneratedRegex(@"^ {0,3}#[ \t]+[^\n]*\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleLine();
}

/// <summary>
/// The JSON shape of <c>wolfden query --json</c>: the canon view, with
/// each field value as JSON (strings as written, lists as arrays, mappings
/// as objects).
/// </summary>
/// <param name="Version">The shape's version, 1.</param>
/// <param name="Universe">The universe's key.</param>
/// <param name="Position">The position as given, or null for the end of the story.</param>
/// <param name="Filter">The series or book the view is narrowed to, or null.</param>
/// <param name="Pinned">True for the pinned canon view (F-VIEW-2).</param>
/// <param name="Entities">The entities, in F-VIEW-3 order.</param>
internal sealed record QueryReport(
    int Version, string Universe, string? Position, QueryReport.FilterInfo? Filter, bool Pinned, IReadOnlyList<QueryReport.Entity> Entities)
{
    /// <summary>A filter.</summary>
    /// <param name="Kind"><c>series</c> or <c>book</c>.</param>
    /// <param name="Key">Its key.</param>
    internal sealed record FilterInfo(string Kind, string Key);

    /// <summary>An entity in the view. The fields follow <see cref="CanonEntity"/>.</summary>
    internal sealed record Entity(
        string Key, string? Id, string Name, string Type, string Status, string ScopeKind, string ScopeKey, string Path,
        string? From, string? Title, string Lead, int LeadLine, IReadOnlyList<Field> Fields, IReadOnlyList<Section> Sections);

    /// <summary>A field value. The fields follow <see cref="CanonFieldValue"/>; <see cref="Value"/> is the value itself as JSON.</summary>
    internal sealed record Field(
        string Name, JsonElement Value, string Kind, int? EntryIndex, string? Status, bool Pinned, string? From, string? Until, string Path, int Line);

    /// <summary>A section. The fields follow <see cref="CanonSection"/>.</summary>
    internal sealed record Section(
        string Name, int Level, IReadOnlyList<string> HeadingPath, string Body, string Status, bool Pinned, string? From, string? Until,
        string Path, int Line);

    public static QueryReport From(CanonView view, bool pinned) => new(
        1,
        view.Universe,
        view.Position,
        view.Filter is null ? null : new FilterInfo(view.Filter.Kind == CanonFilterKind.Series ? "series" : "book", view.Filter.Key),
        pinned,
        view.Entities.Select(e => new Entity(
            e.Key, e.Id, e.Name, e.Type, e.Status, e.ScopeKind, e.ScopeKey, e.Path, e.From, e.Title, e.Lead, e.LeadLine,
            e.Fields.Select(f => new Field(f.Name, Parse(f.ValueJson), f.Kind, f.EntryIndex, f.Status, f.Pinned, f.From, f.Until, f.Path, f.Line)).ToList(),
            e.Sections.Select(s => new Section(s.Name, s.Level, s.HeadingPath, s.Body, s.Status, s.Pinned, s.From, s.Until, s.Path, s.Line)).ToList()))
            .ToList());

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
