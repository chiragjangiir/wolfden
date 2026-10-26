using System.Text;
using System.Text.Json;

namespace Wolfden.Index.Reading;

/// <summary>A field value a search can match: its entity, field, line, value as JSON, and effective status.</summary>
internal sealed record FieldCandidate(EntityRecord Entity, string Field, int Line, string ValueJson, string Status);

/// <summary>A field value a search matched, with its snippet.</summary>
internal sealed record FieldHit(EntityRecord Entity, string Field, int Line, string Status, MarkedText Snippet);

/// <summary>
/// Search in field values (#179, maintainer decision 2026-10-26). A value's
/// words are its own, as written (a link's id or name as typed included),
/// and for each link in it that resolved, the linked note's name and
/// aliases, matched the way the search table matches text
/// (<see cref="SearchWords"/>). As of a position, a linked note's name and
/// aliases count only if the reader has met it, so neither a match nor a
/// snippet gives away a note they haven't.
/// </summary>
/// <remarks>
/// Field values aren't in the search table: what a link's note is called
/// changes when that note is renamed, without the linking file changing,
/// and what counts depends on the position. The values are few next to
/// sections, so they're matched here, when asked.
/// </remarks>
internal sealed class FieldSearch
{
    /// <summary>The most of a long value a snippet shows, in characters.</summary>
    private const int Window = 160;

    private readonly IndexDbContext _db;
    private readonly SearchWords _words;
    private readonly int? _universeId;
    private readonly string? _field;
    private readonly IReadOnlySet<string>? _met;
    private ILookup<(int EntityId, string Field), int>? _lines;
    private ILookup<(int EntityId, string Field, int Line), string[]>? _links;

    /// <summary>A search for <paramref name="words"/>.</summary>
    /// <param name="index">The index.</param>
    /// <param name="words">The query's words.</param>
    /// <param name="universeId">The universe, or null for every universe.</param>
    /// <param name="field">The one field to search, or null for every field.</param>
    /// <param name="met">As of a position, the paths of the notes the reader has met, whatever a filter shows; null without a position.</param>
    public FieldSearch(DenIndex index, SearchWords words, int? universeId, string? field, IReadOnlySet<string>? met)
    {
        _db = index.Context;
        _words = words;
        _universeId = universeId;
        _field = field;
        _met = met;
    }

    /// <summary>Every field value (every status, entry, and range) of the entities <paramref name="keep"/> keeps, by path and then in field order.</summary>
    public IEnumerable<FieldCandidate> AllValues(Func<EntityRecord, bool> keep)
    {
        var values = Values();
        var ids = values.Select(v => v.EntityId).Distinct().ToList();
        var entities = _db.Entities.Where(e => ids.Contains(e.Id)).ToList().Where(keep).ToDictionary(e => e.Id);
        return values
            .Where(v => entities.ContainsKey(v.EntityId))
            .OrderBy(v => entities[v.EntityId].Path, StringComparer.Ordinal).ThenBy(v => v.FieldOrder).ThenBy(v => v.EntryIndex ?? -1)
            .Select(v => new FieldCandidate(entities[v.EntityId], v.Field, v.Line, v.ValueJson, v.Status))
            .ToList();
    }

    /// <summary>The field values of a canon view (not the types' defaults), of the entities <paramref name="keep"/> keeps, in the view's order.</summary>
    public IEnumerable<FieldCandidate> ValuesIn(CanonView view, Func<EntityRecord, bool> keep)
    {
        var paths = view.Entities.Select(e => e.Path).ToList();
        var entities = _db.Entities.Where(e => paths.Contains(e.Path)).ToList().Where(keep).ToDictionary(e => e.Path, StringComparer.Ordinal);
        return view.Entities
            .Where(e => entities.ContainsKey(e.Path))
            .SelectMany(e => e.Fields
                .Where(f => !f.IsDefault && (_field is null || f.Name == _field))
                .Select(f => new FieldCandidate(entities[e.Path], f.Name, f.Line, f.ValueJson, f.Status ?? e.Status)))
            .ToList();
    }

    /// <summary>The hit, if every query word is in the value's words; otherwise null.</summary>
    public FieldHit? Match(FieldCandidate value)
    {
        if (_words.IsEmpty)
        {
            return null;
        }

        var written = ValueText(value.ValueJson);
        var own = _words.Find(written);
        var names = LinkedNames(value).Select(n => (Name: n, Found: _words.Find(n.Text))).ToList();
        for (var i = 0; i < _words.Count; i++)
        {
            if (own[i].Count == 0 && names.All(n => n.Found[i].Count == 0))
            {
                return null;
            }
        }

        // The value as written, cut down around its first match if it's long;
        // then, for each linked note that matched, the name or alias that did.
        var snippet = Cut(written, own.SelectMany(m => m).ToList());
        var text = new StringBuilder(snippet.Text);
        var matches = snippet.Matches.ToList();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in names.Where(n => n.Found.Any(f => f.Count > 0)).GroupBy(n => n.Name.Group))
        {
            // The name or alias that holds the most of the query's words, the name first on a tie.
            var (name, found) = group.MaxBy(n => n.Found.Count(f => f.Count > 0));
            if (!shown.Add(name.Text))
            {
                continue;
            }

            text.Append(" → ");
            var at = text.Length;
            text.Append(name.Text);
            matches.AddRange(found.SelectMany(f => f).Select(m => m with { Start = m.Start + at }));
        }

        return new FieldHit(value.Entity, value.Field, value.Line, value.Status, new MarkedText(text.ToString(), MarkedText.Merge(matches)));
    }

    /// <summary>A field value as one line of text: a list's items and a mapping's keys and values joined with commas.</summary>
    public static string ValueText(string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        return Text(document.RootElement);

        static string Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString()!,
            JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Text)),
            JsonValueKind.Object => string.Join(", ", e.EnumerateObject().Select(p => $"{p.Name}: {Text(p.Value)}")),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => e.GetRawText(),
        };
    }

    /// <summary>
    /// The names and aliases of the notes the value's resolved links reach,
    /// each tagged with its link's place, so a snippet shows one per link.
    /// As of a position, only notes the reader has met.
    /// </summary>
    private IEnumerable<(string Text, int Group)> LinkedNames(FieldCandidate value)
    {
        LoadLinks();
        return _links![(value.Entity.Id, value.Field, value.Line)].SelectMany((names, i) => names.Select(n => (n, i)));
    }

    /// <summary>
    /// For each field value with links, the names and aliases of the notes
    /// they reach. A link belongs to the last value of its field that starts
    /// on or before its line: its entry, or the plain value.
    /// </summary>
    private void LoadLinks()
    {
        if (_links is not null)
        {
            return;
        }

        _lines = Values().ToLookup(v => (v.EntityId, v.Field), v => v.Line);
        var links = _db.Links
            .Where(l => (l.Site == "entity_field" || l.Site == "entity_field_entry") && l.Outcome == "resolved"
                && l.SourceEntityId != null && l.Field != null && l.TargetKey != null
                && (_universeId == null || l.UniverseId == _universeId) && (_field == null || l.Field == _field))
            .Select(l => new { EntityId = l.SourceEntityId!.Value, Field = l.Field!, l.Line, l.Column, l.UniverseId, Target = l.TargetKey! })
            .ToList();
        var keys = links.Select(l => l.Target).Distinct().ToList();
        var targets = _db.Entities.Where(e => keys.Contains(e.Key) && !e.HasDuplicateId).ToList()
            .Where(e => _met is null || _met.Contains(e.Path))
            .ToLookup(e => (e.UniverseId, e.Key));
        var targetIds = targets.SelectMany(t => t).Select(e => e.Id).ToList();
        var aliases = _db.Aliases.Where(a => targetIds.Contains(a.EntityId)).ToList().ToLookup(a => a.EntityId);
        _links = links
            .Select(l => (Link: l, Owner: _lines[(l.EntityId, l.Field)].Where(line => line <= l.Line).DefaultIfEmpty(-1).Max()))
            .Where(x => x.Owner >= 0)
            .OrderBy(x => x.Link.Line).ThenBy(x => x.Link.Column)
            .Select(x => (
                Key: (x.Link.EntityId, x.Link.Field, x.Owner),
                Names: targets[(x.Link.UniverseId, x.Link.Target)]
                    .SelectMany(e => aliases[e.Id].OrderBy(a => a.Order).Select(a => a.Alias).Prepend(e.Name))
                    .ToArray()))
            .Where(x => x.Names.Length > 0)
            .ToLookup(x => x.Key, x => x.Names);
    }

    private List<FieldValueRecord> Values() =>
        _db.FieldValues.Where(v => (_universeId == null || v.UniverseId == _universeId) && (_field == null || v.Field == _field)).ToList();

    /// <summary>A long value cut down to about <see cref="Window"/> characters around its first match, with ellipses where it's cut.</summary>
    internal static MarkedText Cut(string text, IReadOnlyList<TextMatch> matches)
    {
        var merged = MarkedText.Merge(matches);
        if (text.Length <= Window)
        {
            return new MarkedText(text, merged);
        }

        var first = merged.Count > 0 ? merged[0].Start : 0;
        var start = Math.Max(0, first - (Window / 4));
        while (start > 0 && start < first && !char.IsWhiteSpace(text[start - 1]))
        {
            start++;
        }

        var end = Math.Min(text.Length, start + Window);
        while (end < text.Length && end > first && !char.IsWhiteSpace(text[end]))
        {
            end--;
        }

        if (end <= first)
        {
            end = Math.Min(text.Length, start + Window);
        }

        // Never cut a surrogate pair in two: step back to its first half.
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--;
        }

        if (end < text.Length && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        var head = start > 0 ? "…" : "";
        var kept = merged
            .Where(m => m.Start >= start && m.Start + m.Length <= end)
            .Select(m => m with { Start = m.Start - start + head.Length })
            .ToList();
        return new MarkedText(head + text[start..end] + (end < text.Length ? "…" : ""), kept);
    }
}
