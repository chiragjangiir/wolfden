using Wolfden.Core.Documents;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Entities;

/// <summary>A place in story order, with how it's written or what it defaults to, for messages.</summary>
/// <param name="Point">The place in story order.</param>
/// <param name="Description">The position as written, or the start it stands for.</param>
public sealed record FactBound(StoryPoint Point, string Description);

/// <summary>How a field's value is written (format.md F-FLD-5, F-TMP-1).</summary>
public enum FieldValueKind
{
    /// <summary>A plain value, true at every position the entity is.</summary>
    Plain,

    /// <summary>A single entry: a mapping with a <c>value</c> key.</summary>
    Entry,

    /// <summary>A temporal list: a non-empty list of entries.</summary>
    Temporal,
}

/// <summary>One field under an entity's <c>fields</c> (format.md §7.2).</summary>
public sealed class FieldFact
{
    internal FieldFact(string name, int order, FieldValueKind kind, YamlNode value, IReadOnlyList<EntryFact> entries)
    {
        Name = name;
        Order = order;
        Kind = kind;
        Value = value;
        Entries = entries;
    }

    /// <summary>The field name as written.</summary>
    public string Name { get; }

    /// <summary>The field's 0-based place in <c>fields</c>, in frontmatter order (F-VIEW-3).</summary>
    public int Order { get; }

    /// <summary>Plain, a single entry, or a temporal list.</summary>
    public FieldValueKind Kind { get; }

    /// <summary>The value as parsed: the plain value, the entry, or the list.</summary>
    public YamlNode Value { get; }

    /// <summary>The entries in file order, or none for a plain value.</summary>
    public IReadOnlyList<EntryFact> Entries { get; }
}

/// <summary>
/// A field entry (format.md §7.3) with its status, pin, and the range of
/// story positions where it holds (F-TMP-3, F-TMP-4).
/// </summary>
public sealed class EntryFact
{
    internal EntryFact(YamlMapping node, int index)
    {
        Node = node;
        Index = index;
    }

    /// <summary>The entry mapping as parsed.</summary>
    public YamlMapping Node { get; }

    /// <summary>The entry's 0-based place in its field, in file order (the F-TMP-5 tie-break).</summary>
    public int Index { get; }

    /// <summary>The entry's <c>value</c>.</summary>
    public YamlNode? Value => Node["value"];

    /// <summary>The <c>from</c> as written, or null if it has none.</summary>
    public string? FromText { get; internal set; }

    /// <summary>The <c>until</c> as written, or null if it has none.</summary>
    public string? UntilText { get; internal set; }

    /// <summary>The status written on the entry, or what it takes from its entity (F-STAT-2).</summary>
    public FactStatus OwnStatus { get; internal set; }

    /// <summary>The effective status (F-STAT-3).</summary>
    public FactStatus Status { get; internal set; }

    /// <summary>True if the entry has <c>pin: true</c> (F-PIN-1).</summary>
    public bool Pinned { get; internal set; }

    /// <summary>True if its <c>from</c> and <c>until</c> resolve, or it has none (F-POS-4).</summary>
    public bool PositionsResolve { get; internal set; }

    /// <summary>True if it has an explicit <c>until</c> that isn't after its <c>from</c> (F-TMP-3).</summary>
    public bool IsEmptyRange { get; internal set; }

    /// <summary>True if the entry is live: in canon, its positions resolve, and its range isn't empty (F-TMP-4).</summary>
    public bool IsLive => Status.IsInCanon() && PositionsResolve && !IsEmptyRange;

    /// <summary>Where the entry starts: its <c>from</c>, or the start of the story. Null if a position doesn't resolve.</summary>
    public StoryPoint? EffectiveFrom { get; internal set; }

    /// <summary>
    /// Where the entry ends (exclusive): its <c>until</c>, or for a live entry
    /// without one the <c>from</c> of the next live entry, or else the end of
    /// the story (F-TMP-4). Null if a position doesn't resolve.
    /// </summary>
    public StoryPoint? EffectiveUntil { get; internal set; }
}

/// <summary>
/// A section of an entity (format.md §9) with its status, pin,
/// bookkeeping, and the range of story positions where it's in effect.
/// </summary>
public sealed class SectionFact
{
    internal SectionFact(Section section)
    {
        Section = section;
    }

    /// <summary>The section.</summary>
    public Section Section { get; }

    /// <summary>The status written in its comment, or what it takes from its holder (F-STAT-2).</summary>
    public FactStatus OwnStatus { get; internal set; }

    /// <summary>The effective status (F-STAT-3).</summary>
    public FactStatus Status { get; internal set; }

    /// <summary>True if the section or a section holding it has <c>pin=true</c> (F-PIN-1, F-PIN-2).</summary>
    public bool Pinned { get; internal set; }

    /// <summary>True for a bookkeeping section (F-SEC-12).</summary>
    public bool IsBookkeeping { get; internal set; }

    /// <summary>The comment's <c>from</c> as written, or null.</summary>
    public string? FromText { get; internal set; }

    /// <summary>The comment's <c>until</c> as written, or null.</summary>
    public string? UntilText { get; internal set; }

    /// <summary>
    /// The effective <c>from</c> (F-SEC-8, F-SEC-9): the later of its own
    /// <c>from</c> and its parent's, a level-2 section without one taking
    /// the entity's (or a thread's <c>## Payoff</c> its <c>paid_off</c>,
    /// types.md F-CORE-4). Null if a position on the way doesn't resolve.
    /// </summary>
    public FactBound? EffectiveFrom { get; internal set; }

    /// <summary>
    /// The effective <c>until</c> (exclusive, F-SEC-8, F-SEC-9): its own
    /// <c>until</c>; or for a live standard level-2 section, the
    /// earliest <c>from</c> after its own among the other live level-2
    /// sections with the same name (it <b>hands over</b>); or else the end
    /// of the story. A subsection's is never after its parent's. Null if a
    /// position on the way doesn't resolve.
    /// </summary>
    public StoryPoint? EffectiveUntil { get; internal set; }

    /// <summary>
    /// True for a level-2 section that is one of the standard sections of
    /// the entity's type (types.md F-TYPE-10), which hands over to the next
    /// live one with the same name (F-SEC-8). Always false for a subsection,
    /// and for every section of an untyped entity (F-ENT-2).
    /// </summary>
    public bool IsStandard { get; internal set; }

    /// <summary>
    /// True if the section is live (F-SEC-8): in canon (F-STAT-3), not
    /// bookkeeping (F-SEC-12), its positions resolve (F-POS-4), and its
    /// range isn't empty. For a subsection, the range is its effective one
    /// (F-SEC-9), so a subsection that is never in effect isn't live.
    /// </summary>
    public bool IsLive => Status.IsInCanon() && !IsBookkeeping && EffectiveFrom is { } from && EffectiveUntil is { } until
        && from.Point < until;
}

/// <summary>
/// The facts of one entity (format.md §7.3, §9.3): its fields and entries
/// and its sections, each with its effective status, pin, and range of
/// story positions. It's what canon views and the index are built from.
/// </summary>
public sealed class EntityFacts
{
    private readonly Dictionary<Section, SectionFact> _sections = [];

    private EntityFacts(CatalogEntity entity, Bookkeeping bookkeeping)
    {
        Entity = entity;
        Bookkeeping = bookkeeping;
        Fields = ReadFields();

        // F-SEC-8: the level-2 sections first, so standard ones can hand over
        // before their subsections take their ranges (F-SEC-9).
        foreach (var section in entity.File.TopLevelSections)
        {
            Fact(section);
        }

        HandOver();
        Sections = entity.File.Sections.Select(Fact).ToList();
    }

    /// <summary>The entity.</summary>
    public CatalogEntity Entity { get; }

    /// <summary>The fields, in frontmatter order.</summary>
    public IReadOnlyList<FieldFact> Fields { get; }

    /// <summary>The sections, in file order, subsections included.</summary>
    public IReadOnlyList<SectionFact> Sections { get; }

    private Bookkeeping Bookkeeping { get; }

    /// <summary>Works out the facts of <paramref name="entity"/>, with a den's bookkeeping patterns (F-SEC-12).</summary>
    public static EntityFacts For(CatalogEntity entity, Bookkeeping bookkeeping)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(bookkeeping);
        return new EntityFacts(entity, bookkeeping);
    }

    /// <summary>The facts of one of the entity's sections.</summary>
    public SectionFact this[Section section] => Fact(section);

    private SectionFact Fact(Section section)
    {
        if (_sections.TryGetValue(section, out var fact))
        {
            return fact;
        }

        var parent = section.Parent is { } p ? Fact(p) : null;
        fact = new SectionFact(section)
        {
            OwnStatus = OwnStatus(section),
            Pinned = section.Comment?["pin"] == "true" || parent?.Pinned == true,
            IsBookkeeping = Bookkeeping.IsBookkeeping(section),
            FromText = Bound(section.Comment?["from"]),
            UntilText = Bound(section.Comment?["until"]),
        };
        fact.Status = FactStatuses.Effective(fact.OwnStatus, parent?.Status ?? Entity.Status);
        (fact.EffectiveFrom, fact.EffectiveUntil) = Range(section, fact, parent);
        _sections[section] = fact;
        return fact;
    }

    private (FactBound? From, StoryPoint? Until) Range(Section section, SectionFact fact, SectionFact? parent)
    {
        var from = EffectiveFrom(section, fact, parent);
        if (from is null)
        {
            return (null, null);
        }

        var until = fact.UntilText is { } untilText ? Resolve(untilText) : Entity.Universe.EndOfStory;
        if (until is null)
        {
            return (from, null);
        }

        if (parent is not null)
        {
            until = parent.EffectiveUntil is { } parentUntil ? StoryPoint.Min(until.Value, parentUntil) : null;
        }

        return (from, until);
    }

    /// <summary>
    /// F-SEC-8, F-SEC-9. It doesn't depend on <c>until</c>, so a link's
    /// F-LINK-13 position survives an <c>until</c> that doesn't resolve.
    /// </summary>
    private FactBound? EffectiveFrom(Section section, SectionFact fact, SectionFact? parent)
    {
        var fromText = fact.FromText ?? (parent is null && IsPayoff(section) ? PaidOff() : null);
        FactBound? own = null;

        // A from that doesn't parse counts as unset (types.md F-FT-10). That
        // can only be a thread's paid_off here: a bad from= in a section
        // comment is an error that excludes the file. A well-formed position
        // that doesn't resolve leaves the section with no range (F-POS-4).
        if (fromText is not null && StoryPosition.TryParse(fromText, out var position, out _))
        {
            if (Entity.Universe.Resolve(position).Resolved is not { } resolved)
            {
                return null;
            }

            own = new FactBound(resolved.Point, fromText);
        }

        if (parent is null)
        {
            return own ?? (Entity.EntersCanon is { } enters ? new FactBound(enters, Entity.EntersCanonDescription) : null);
        }

        if (parent.EffectiveFrom is not { } inherited)
        {
            return null;
        }

        return own is null || own.Point < inherited.Point ? inherited : own;
    }

    /// <summary>
    /// F-SEC-8: a live standard level-2 section without an <c>until</c>
    /// ends at the earliest <c>from</c> after its own among the other live
    /// level-2 sections with the same name. Sections that share a
    /// <c>from</c> overlap, and neither ends the other (F-SEC-4).
    /// </summary>
    private void HandOver()
    {
        if (Entity.Definition is not { } type)
        {
            return;
        }

        var standard = Entity.File.TopLevelSections
            .Where(s => type.IsStandardSection(s.Name))
            .Select(s => _sections[s])
            .ToList();
        foreach (var fact in standard)
        {
            fact.IsStandard = true;
        }

        foreach (var group in standard.Where(f => f.IsLive).GroupBy(f => LinkText.Normalize(f.Section.Name), StringComparer.Ordinal))
        {
            var live = group.ToList();
            foreach (var fact in live.Where(f => f.UntilText is null))
            {
                var from = fact.EffectiveFrom!.Point;
                var next = live.Select(f => f.EffectiveFrom!.Point).Where(p => p > from).DefaultIfEmpty(Entity.Universe.EndOfStory).Min();
                fact.EffectiveUntil = next;
            }
        }
    }

    /// <summary>F-STAT-2: a section without a status takes its parent's, and a level-2 one its entity's.</summary>
    private FactStatus OwnStatus(Section section) =>
        section.Comment?["status"] is { } s && FactStatuses.Parse(s) is { } parsed ? parsed
        : section.Parent is { } parent ? OwnStatus(parent)
        : Entity.Status;

    /// <summary>F-CORE-4: a level-2 <c>## Payoff</c> of an entity whose type is the built-in core pack's thread.</summary>
    private bool IsPayoff(Section section) =>
        Entity.Definition?.IsCoreThread == true && LinkText.Normalize(section.Name) == "payoff";

    /// <summary>
    /// A thread's <c>paid_off</c>, read from a plain value or a single
    /// in-canon entry (types.md §9.1). Anything else counts as unset.
    /// </summary>
    private string? PaidOff() =>
        (Entity.File.Frontmatter?["fields"] as YamlMapping)?["paid_off"] switch
        {
            YamlScalar { IsNull: false } plain => Bound(plain.Text),
            YamlMapping entry when EntityCatalog.IsEntry(entry) && EntryStatus(entry).Status.IsInCanon()
                && entry["value"] is YamlScalar { IsNull: false } value => Bound(value.Text),
            _ => null,
        };

    private List<FieldFact> ReadFields()
    {
        var result = new List<FieldFact>();
        if (Entity.File.Frontmatter?["fields"] is not YamlMapping fields)
        {
            return result;
        }

        foreach (var field in fields.Entries)
        {
            if (field.KeyText is not { } name)
            {
                continue;
            }

            var (kind, nodes) = field.Value switch
            {
                YamlMapping map when EntityCatalog.IsEntry(map) => (FieldValueKind.Entry, new List<YamlMapping> { map }),
                YamlSequence { Items.Count: > 0 } list when list.Items.All(EntityCatalog.IsEntry) =>
                    (FieldValueKind.Temporal, list.Items.Cast<YamlMapping>().ToList()),
                _ => (FieldValueKind.Plain, []),
            };

            result.Add(new FieldFact(name, result.Count, kind, field.Value, Entries(nodes)));
        }

        return result;
    }

    private List<EntryFact> Entries(List<YamlMapping> nodes)
    {
        var entries = new List<EntryFact>();
        foreach (var node in nodes)
        {
            var entry = new EntryFact(node, entries.Count)
            {
                FromText = Bound(node["from"]),
                UntilText = Bound(node["until"]),
                Pinned = node["pin"] is YamlScalar pin && pin.AsBoolean() == true,
            };
            (entry.OwnStatus, entry.Status) = EntryStatus(node);
            var from = entry.FromText is { } f ? Resolve(f) : Entity.Universe.StartOfStory;
            var until = entry.UntilText is { } u ? Resolve(u) : null;
            entry.PositionsResolve = from is not null && (entry.UntilText is null || until is not null);
            entry.IsEmptyRange = entry.PositionsResolve && until is { } end && end <= from!.Value;
            entry.EffectiveFrom = entry.PositionsResolve ? from : null;
            entry.EffectiveUntil = entry.PositionsResolve ? until : null;
            entries.Add(entry);
        }

        // F-TMP-4: a live entry without until holds until the next live entry's from.
        var live = entries.Where(e => e.IsLive).ToList();
        foreach (var entry in entries.Where(e => e.PositionsResolve && e.UntilText is null))
        {
            var next = entry.IsLive
                ? live.Select(e => e.EffectiveFrom!.Value).Where(p => p > entry.EffectiveFrom!.Value).DefaultIfEmpty(Entity.Universe.EndOfStory).Min()
                : Entity.Universe.EndOfStory;
            entry.EffectiveUntil = next;
        }

        return entries;
    }

    /// <summary>An entry's own and effective status (F-STAT-2, F-STAT-3).</summary>
    internal (FactStatus Own, FactStatus Status) EntryStatus(YamlMapping entry)
    {
        var own = entry["status"] is YamlScalar { IsNull: false } s && FactStatuses.Parse(s.Text) is { } parsed ? parsed : Entity.Status;
        return (own, FactStatuses.Effective(own, Entity.Status));
    }

    private StoryPoint? Resolve(string text) =>
        StoryPosition.TryParse(text, out var position, out _) && Entity.Universe.Resolve(position).Resolved is { } resolved
            ? resolved.Point
            : null;

    /// <summary>A <c>from</c> or <c>until</c> as written, or null if it's missing, empty, or null (F-TMP-2, F-SEC-7).</summary>
    private static string? Bound(YamlNode? node) => node is YamlScalar { IsNull: false } s ? Bound(s.Text) : null;

    private static string? Bound(string? text) => text is null || PositionScanner.IsEmptyBound(text) ? null : text;
}
