using Wolfden.Core.Documents;
using Wolfden.Core.Entities;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Checks;

/// <summary>
/// The continuity checks that only <c>wolfden check</c> runs (design.md §7):
/// the warnings that loading a den doesn't already report. Loading reports
/// parse errors, ids, links, and spoilers (<see cref="DenStructure"/>,
/// <see cref="EntityCatalog"/>, <see cref="LinkIndex"/>); these add
/// unknown types (F-ENT-2), missing required fields and sections (F-FLD-3,
/// F-SEC-5), field values (<see cref="FieldChecks"/>), positions that don't
/// resolve (F-POS-4), a book-scope entity's <c>from</c> outside its book
/// (F-ENT-6), empty ranges (F-TMP-3, F-SEC-8), overlapping values and
/// sections (F-TMP-5, F-SEC-4), subsections outside their parents (F-SEC-9),
/// statuses and pins (F-STAT-3, F-PIN-3), section comments under or on
/// bookkeeping sections (F-SEC-7, F-SEC-12), a thread paid off before it's
/// planted (F-CORE-2), an event that ends before it starts (F-CORE-3), and
/// for container files, a missing id (F-ID-2) and unknown authors (F-CTR-2).
/// </summary>
internal static class ContinuityChecks
{
    public static List<DenDiagnostic> Run(EntityCatalog catalog, IDenSource source, RowIndex rows)
    {
        var diagnostics = new List<DenDiagnostic>();
        var bookkeeping = new Bookkeeping(catalog.Structure.BookkeepingSections);
        foreach (var entity in catalog.Entities)
        {
            var found = new List<Diagnostic>();
            CheckType(entity, source, found);
            FieldChecks.Run(entity, rows, found);
            CheckPositions(entity.Universe, PositionScanner.Scan(entity.File, entity.Definition?.PositionFields), found);
            CheckFrom(entity, found);
            var facts = EntityFacts.For(entity, bookkeeping);
            CheckEntries(facts, found);
            CheckSections(facts, found);
            CheckSubsections(facts, found);
            CheckStatuses(facts, found);
            CheckBookkeeping(entity.File.Sections, s => facts[s].IsBookkeeping, found);
            CheckThread(facts, found);
            CheckEvent(facts, found);
            diagnostics.AddRange(found.Select(d => new DenDiagnostic(entity.Path, d)));
        }

        foreach (var universe in catalog.Structure.Universes)
        {
            foreach (var container in new Container[] { universe }.Concat(universe.Series).Concat(universe.Books))
            {
                var found = new List<Diagnostic>();
                CheckId(container, found);
                CheckAuthors(container, catalog.Structure, found);
                CheckBookkeeping(container.File.Sections, bookkeeping.IsBookkeeping, found);
                CheckPositions(universe, PositionScanner.ScanContainer(container.File), found);
                diagnostics.AddRange(found.Select(d => new DenDiagnostic(container.FilePath, d)));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// F-ID-2 for a container file: no id is a warning, worded as for an
    /// entity (<see cref="EntityCatalog"/>). Containers aren't link targets
    /// in v0 (F-LINK-5), so the message doesn't mention links.
    /// </summary>
    private static void CheckId(Container container, List<Diagnostic> found)
    {
        if (container.Id is not null)
        {
            return;
        }

        var kind = container.Kind.ToString().ToLowerInvariant();
        found.Add(new Diagnostic("F-ID-2", DiagnosticSeverity.Warning, 1, container is Book { Status: BookStatus.Final } frozen
            ? $"no id, and book '{frozen.Key}' is final (frozen), so `wolfden check --fix` won't add one until the book is unfrozen. Until then, the index knows this {kind} by its path."
            : $"no id; run `wolfden check --fix` to add one. Until then, the index knows this {kind} by its path."));
    }

    /// <summary>
    /// F-CTR-2: a container's <c>authors</c> is a list of author keys from
    /// <c>den.yaml</c> (F-DEN-3). An unknown key is a warning. So is any
    /// other shape: the spec gives it no severity, and an error would leave
    /// the container and everything under it out (F-GEN-2).
    /// </summary>
    private static void CheckAuthors(Container container, DenStructure den, List<Diagnostic> found)
    {
        var authors = den.Authors;
        var node = container.File.Frontmatter?["authors"];
        if (node is null or YamlScalar { IsNull: true })
        {
            return;
        }

        if (node is not YamlSequence list)
        {
            found.Add(new Diagnostic("F-CTR-2", DiagnosticSeverity.Warning, node.Line,
                "authors must be a list of author keys from den.yaml, such as 'authors: [wren-calder]', even for one author, so it's ignored."));
            return;
        }

        foreach (var item in list.Items)
        {
            if (item is not YamlScalar { IsNull: false } key)
            {
                found.Add(new Diagnostic("F-CTR-2", DiagnosticSeverity.Warning, item.Line, "Each entry in authors must be an author key from den.yaml. This one is ignored."));
            }
            else if (den.SkippedAuthors.Contains(key.Text, StringComparer.Ordinal))
            {
                found.Add(new Diagnostic("F-CTR-2", DiagnosticSeverity.Warning, item.Line,
                    $"'{key.Text}' is in den.yaml, but its identity there isn't valid, so the author is skipped (see the warning in den.yaml)."));
            }
            else if (!authors.Contains(key.Text, StringComparer.Ordinal))
            {
                var known = authors.Count == 0 ? "den.yaml has no authors" : $"den.yaml's authors are {string.Join(", ", authors)}";
                found.Add(new Diagnostic("F-CTR-2", DiagnosticSeverity.Warning, item.Line, $"'{key.Text}' isn't an author in den.yaml ({known})."));
            }
        }
    }

    /// <summary>F-ENT-6: a book-scope entity whose <c>from</c> is outside its own book.</summary>
    private static void CheckFrom(CatalogEntity entity, List<Diagnostic> found)
    {
        if (entity.Scope is Book book && entity.FromResolution?.Resolved is { } from && from.Book != book)
        {
            found.Add(new Diagnostic("F-ENT-6", DiagnosticSeverity.Warning, entity.File.Frontmatter?["from"]?.Line,
                $"from {entity.From} is in book '{from.Book.Key}', but this entity is filed in book '{book.Key}'. Views still use it, but an entity of a book should enter canon in that book: check the position, or file the entity in the scope where the reader meets it."));
        }
    }

    /// <summary>
    /// F-SEC-9: a subsection whose own <c>from</c> or <c>until</c> lies
    /// outside its parent's effective range. It's still read: it's in effect
    /// only where its parent is. Skipped where a position doesn't resolve
    /// (F-POS-4), and where a range is already empty (F-SEC-8).
    /// </summary>
    private static void CheckSubsections(EntityFacts facts, List<Diagnostic> found)
    {
        var universe = facts.Entity.Universe;
        foreach (var fact in facts.Sections.Where(f => f.Section.Parent is not null && !f.IsEmptyRange))
        {
            var parent = facts[fact.Section.Parent!];
            if (parent.IsEmptyRange || parent.EffectiveFrom is not { } parentFrom || parent.EffectiveUntil is not { } parentUntil
                || parentFrom.Point >= parentUntil)
            {
                continue;
            }

            var line = fact.Section.Comment?.Line ?? fact.Section.Line;
            var name = fact.Section.Name;
            var holder = parent.Section.Name;
            if (fact.FromText is { } fromText && Resolve(universe, fromText) is { } from)
            {
                if (from < parentFrom.Point)
                {
                    found.Add(new Diagnostic("F-SEC-9", DiagnosticSeverity.Warning, line,
                        $"'{name}': from {fromText} is before its parent '{holder}' starts ({parentFrom.Description}), so the subsection starts with its parent instead."));
                }
                else if (from >= parentUntil)
                {
                    found.Add(new Diagnostic("F-SEC-9", DiagnosticSeverity.Warning, line,
                        $"'{name}': from {fromText} isn't before its parent '{holder}' ends, so the subsection is never in effect."));
                }
            }

            if (fact.UntilText is { } untilText && Resolve(universe, untilText) is { } until)
            {
                if (until > parentUntil)
                {
                    found.Add(new Diagnostic("F-SEC-9", DiagnosticSeverity.Warning, line,
                        $"'{name}': until {untilText} is after its parent '{holder}' ends, so the subsection ends with its parent instead."));
                }
                else if (until <= parentFrom.Point)
                {
                    found.Add(new Diagnostic("F-SEC-9", DiagnosticSeverity.Warning, line,
                        $"'{name}': until {untilText} isn't after its parent '{holder}' starts ({parentFrom.Description}), so the subsection is never in effect."));
                }
            }
        }
    }

    /// <summary>
    /// F-STAT-3: a fact whose written status is <c>draft</c> or <c>canon</c>
    /// inside an out-of-canon holder. Only a status written on the fact
    /// itself counts, so one misplaced status gives one warning, not one per
    /// subsection that takes it (F-STAT-2). F-PIN-3: a fact with
    /// <c>pin=true</c> or <c>pin: true</c> written on it whose effective
    /// status is out of canon. It's skipped where F-STAT-3 already says why
    /// the fact is out of canon, and for a bookkeeping section, which is
    /// F-SEC-12's.
    /// </summary>
    private static void CheckStatuses(EntityFacts facts, List<Diagnostic> found)
    {
        var entity = facts.Entity;
        foreach (var fact in facts.Sections)
        {
            var section = fact.Section;
            if (section.Comment is not { } comment)
            {
                continue;
            }

            var holder = section.Parent is { } parent ? facts[parent].Status : entity.Status;
            var holderName = section.Parent is { } p ? $"its parent '{p.Name}'" : "the entity";
            var raised = comment["status"] is { } written && FactStatuses.Parse(written) is { } own && own.IsInCanon() && !holder.IsInCanon();
            if (raised)
            {
                found.Add(new Diagnostic("F-STAT-3", DiagnosticSeverity.Warning, comment.Line,
                    $"'{section.Name}' is marked {comment["status"]}, but {holderName} is {holder.ToKey()}, so it's read as {holder.ToKey()}: a fact can't be more canon than what holds it."));
            }

            if (!raised && comment["pin"] == "true" && !fact.IsBookkeeping && !fact.Status.IsInCanon())
            {
                found.Add(new Diagnostic("F-PIN-3", DiagnosticSeverity.Warning, comment.Line,
                    $"'{section.Name}' is pinned, but it's {fact.Status.ToKey()}, which is out of canon, so pinned canon leaves it out."));
            }
        }

        foreach (var field in facts.Fields)
        {
            foreach (var entry in field.Entries)
            {
                var raised = entry.Node["status"] is YamlScalar { IsNull: false } written && FactStatuses.Parse(written.Text) is { } own
                    && own.IsInCanon() && !entity.Status.IsInCanon();
                if (raised)
                {
                    found.Add(new Diagnostic("F-STAT-3", DiagnosticSeverity.Warning, entry.Node["status"]!.Line,
                        $"{field.Name}: this entry is marked {entry.OwnStatus.ToKey()}, but the entity is {entity.Status.ToKey()}, so it's read as {entity.Status.ToKey()}: a fact can't be more canon than what holds it."));
                }

                if (!raised && entry.Pinned && !entry.Status.IsInCanon())
                {
                    found.Add(new Diagnostic("F-PIN-3", DiagnosticSeverity.Warning, entry.Node["pin"]!.Line,
                        $"{field.Name}: this entry is pinned, but it's {entry.Status.ToKey()}, which is out of canon, so pinned canon leaves it out."));
                }
            }
        }
    }

    /// <summary>
    /// F-SEC-7: <c>canon=true</c> on a subsection whose parent is
    /// bookkeeping does nothing. F-SEC-12: <c>pin=true</c> on a bookkeeping
    /// section, which pinned canon leaves out. Both are warnings on the
    /// section comment where they're written.
    /// </summary>
    private static void CheckBookkeeping(IEnumerable<Section> sections, Func<Section, bool> isBookkeeping, List<Diagnostic> found)
    {
        foreach (var section in sections)
        {
            if (section.Comment is not { } comment)
            {
                continue;
            }

            if (comment["canon"] == "true" && section.Parent is { } parent && isBookkeeping(parent))
            {
                found.Add(new Diagnostic("F-SEC-7", DiagnosticSeverity.Warning, comment.Line,
                    $"'{section.Name}': canon=true does nothing here, since its parent '{parent.Name}' is bookkeeping, and so are its subsections. To keep it in the story, move it out of '{parent.Name}'."));
            }

            if (comment["pin"] == "true" && isBookkeeping(section))
            {
                found.Add(new Diagnostic("F-SEC-12", DiagnosticSeverity.Warning, comment.Line,
                    $"'{section.Name}' is a bookkeeping section, so pin=true does nothing: pinned canon leaves bookkeeping out."));
            }
        }
    }

    /// <summary>
    /// types.md F-CORE-2: a thread whose <c>paid_off</c> comes before its
    /// earliest <c>planted</c> position. Both are read as for F-CORE-4 (a
    /// plain value, or a single in-canon entry), and positions that don't
    /// resolve are skipped. Only the built-in core pack's thread, extended
    /// or not, as for F-CORE-4. When either side is a bare book position
    /// (F-POS-7), only the books are compared, since the chapter isn't known.
    /// </summary>
    private static void CheckThread(EntityFacts facts, List<Diagnostic> found)
    {
        var universe = facts.Entity.Universe;
        if (facts.Entity.Definition?.IsCoreThread != true
            || CoreValue(facts, "paid_off") is not YamlScalar { IsNull: false } paid || ResolvePosition(universe, paid.Text) is not { } paidOff)
        {
            return;
        }

        IReadOnlyList<YamlNode> planted = CoreValue(facts, "planted") switch
        {
            YamlSequence list => list.Items,
            { } one => [one],
            null => [],
        };
        var earliest = planted
            .OfType<YamlScalar>()
            .Where(s => !s.IsNull)
            .Select(s => (s.Text, Resolved: ResolvePosition(universe, s.Text)))
            .Where(p => p.Resolved is not null)
            .OrderBy(p => p.Resolved!.Point)
            .FirstOrDefault();
        if (earliest.Resolved is { } first && IsBefore(universe, paidOff, first))
        {
            found.Add(new Diagnostic("F-CORE-2", DiagnosticSeverity.Warning, paid.Line,
                $"paid_off {paid.Text} comes before the thread is first planted, at {earliest.Text}, in story order. Check the positions."));
        }
    }

    /// <summary>
    /// types.md F-CORE-3: an event whose <c>ends</c> is earlier than its
    /// <c>when</c>, comparing the sort keys only over the numbers both have.
    /// Dates without sort keys are skipped. Both are read as for F-CORE-2,
    /// and only for the built-in core pack's event, extended or not.
    /// </summary>
    private static void CheckEvent(EntityFacts facts, List<Diagnostic> found)
    {
        if (facts.Entity.Definition?.IsCoreEvent != true
            || CoreValue(facts, "when") is not { } whenNode || FieldValues.ReadDate(whenNode) is not { SortKey: { } when } whenDate
            || CoreValue(facts, "ends") is not { } endsNode || FieldValues.ReadDate(endsNode) is not { SortKey: { } ends } endsDate)
        {
            return;
        }

        var shared = Math.Min(when.Count, ends.Count);
        var order = when.Take(shared).Zip(ends.Take(shared), (w, e) => e.CompareTo(w)).FirstOrDefault(c => c != 0);
        if (order < 0)
        {
            found.Add(new Diagnostic("F-CORE-3", DiagnosticSeverity.Warning, endsNode.Line,
                $"ends {DateLabel(endsDate)} is earlier than when {DateLabel(whenDate)}, so the event ends before it starts. Check the dates."));
        }
    }

    /// <summary>
    /// True if <paramref name="later"/> comes before <paramref name="earlier"/>
    /// in story order. If either is a bare book position (F-POS-7), only
    /// their books are compared: a book that is strictly earlier.
    /// </summary>
    private static bool IsBefore(Universe universe, ResolvedPosition later, ResolvedPosition earlier)
    {
        if (!later.Position.IsBookStart && !earlier.Position.IsBookStart)
        {
            return later.Point < earlier.Point;
        }

        return BookStart(universe, later.Book) is { } laterBook && BookStart(universe, earlier.Book) is { } earlierBook && laterBook < earlierBook;
    }

    private static StoryPoint? BookStart(Universe universe, Book book) =>
        universe.Resolve(StoryPosition.Create(book.Key)).Resolved?.Point;

    private static ResolvedPosition? ResolvePosition(Universe universe, string text) =>
        StoryPosition.TryParse(text, out var position, out _) ? universe.Resolve(position).Resolved : null;

    private static string DateLabel(InWorldDate date) => date.Text ?? $"sort [{string.Join(", ", date.SortKey!)}]";

    /// <summary>
    /// A field's value as types.md §9.1 reads <c>planted</c> and
    /// <c>paid_off</c>: a plain value, or the <c>value</c> of a single entry
    /// that is in canon (F-STAT-3). Anything else, such as a temporal list,
    /// counts as unset, and gives null.
    /// </summary>
    private static YamlNode? CoreValue(EntityFacts facts, string name) =>
        facts.Fields.FirstOrDefault(f => f.Name == name) switch
        {
            { Kind: FieldValueKind.Plain, Value: var value } => value is YamlScalar { IsNull: true } ? null : value,
            { Kind: FieldValueKind.Entry, Entries: [{ } entry] } when entry.Status.IsInCanon() => entry.Value is YamlScalar { IsNull: true } ? null : entry.Value,
            _ => null,
        };

    private static StoryPoint? Resolve(Universe universe, string text) =>
        StoryPosition.TryParse(text, out var position, out _) && universe.Resolve(position).Resolved is { } resolved ? resolved.Point : null;

    /// <summary>
    /// F-ENT-2: an unknown type is a warning, and the entity is untyped.
    /// For a known type, F-FLD-3 with F-FT-14 (a missing required field) and
    /// F-SEC-5 with F-TYPE-11 (a missing required section).
    /// </summary>
    private static void CheckType(CatalogEntity entity, IDenSource source, List<Diagnostic> found)
    {
        var fm = entity.File.Frontmatter!;
        var typeLine = fm["type"]?.Line ?? 1;
        if (entity.Definition is not { } type)
        {
            var den = entity.Universe.Den;
            var why = ExcludedBy(den, source, entity.Type) is { } path
                ? $"{path} has an error, so the type didn't load (types.md F-PACK-2). Until it's fixed, the entity is untyped and isn't checked against a type."
                : IgnoredBy(den, entity.Type) is { } clash
                ? $"{clash} defines it, but that definition is ignored (see the warning there). Until that's settled, the entity is untyped and isn't checked against a type."
                : "no enabled type has that key, so the entity is untyped and isn't checked against a type. Check the spelling. If a pack defines it, check that den.yaml enables the pack and that the pack loads without errors.";
            found.Add(new Diagnostic("F-ENT-2", DiagnosticSeverity.Warning, typeLine, $"unknown type '{entity.Type}': {why}"));
            return;
        }

        var fields = fm["fields"] as YamlMapping;
        foreach (var field in type.Fields.Where(f => f.IsRequired))
        {
            var entry = fields?.Entries.FirstOrDefault(e => e.KeyText == field.Name);
            if (entry is null || !HasValue(entry.Value))
            {
                found.Add(new Diagnostic("F-FLD-3", DiagnosticSeverity.Warning, entry?.Key.Line ?? typeLine,
                    $"{field.Name} is required for type '{type.Key}', but it has no value."));
            }
        }

        foreach (var section in type.Sections.Where(s => s.IsRequired))
        {
            if (!entity.File.TopLevelSections.Any(s => type.FindSection(s.Name) == section))
            {
                found.Add(new Diagnostic("F-SEC-5", DiagnosticSeverity.Warning, typeLine,
                    $"'## {section.Name}' is required for type '{type.Key}', but this entity has no such section."));
            }
        }
    }

    /// <summary>
    /// The pack file whose error keeps <paramref name="typeKey"/> from loading
    /// (types.md F-PACK-2): the type's own file, or the <c>pack.yaml</c> of an
    /// enabled pack that has a file for the type. Null if neither, so the type
    /// is really unknown.
    /// </summary>
    private static string? ExcludedBy(DenStructure den, IDenSource source, string typeKey)
    {
        if (!Keys.IsKey(typeKey))
        {
            return null;
        }

        var failed = den.Types.Diagnostics
            .Where(d => d.Diagnostic.Severity == DiagnosticSeverity.Error && d.Path.StartsWith("packs/", StringComparison.Ordinal))
            .Select(d => d.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return failed.FirstOrDefault(p => p.EndsWith($"/types/{typeKey}.yaml", StringComparison.Ordinal))
            ?? failed.FirstOrDefault(p => p.EndsWith("/pack.yaml", StringComparison.Ordinal)
                && (source.TryReadFile($"{p[..^"pack.yaml".Length]}types/{typeKey}.yaml") is not null || ShadowsBuiltInType(p, typeKey)));
    }

    /// <summary>
    /// True if the den-local <c>pack.yaml</c> at <paramref name="packFile"/>
    /// shadows a built-in pack that defines <paramref name="typeKey"/>. With
    /// an error in it, the den has neither pack's types (F-PACK-10).
    /// </summary>
    private static bool ShadowsBuiltInType(string packFile, string typeKey)
    {
        var parts = packFile.Split('/');
        return parts is ["packs", var key, "pack.yaml"]
            && BuiltInPacks.Instance.Keys.Contains(key, StringComparer.Ordinal)
            && BuiltInPacks.Instance.FileExists($"{key}/types/{typeKey}.yaml");
    }

    /// <summary>
    /// The type file whose definition of <paramref name="typeKey"/> was
    /// ignored for a plural clash (types.md F-TYPE-5), so no enabled type
    /// has the key, or null. The clash warning names the ignored type.
    /// </summary>
    private static string? IgnoredBy(DenStructure den, string typeKey) =>
        den.Types.Diagnostics
            .Where(d => d.Diagnostic.RuleId == "F-TYPE-5"
                && d.Diagnostic.Message.Contains($"type '{typeKey}' is ignored", StringComparison.Ordinal))
            .Select(d => d.Path)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// F-FT-14: a field has a value if it has a plain value that isn't null,
    /// or an entry whose value isn't null. An empty list is unset (F-FT-12).
    /// </summary>
    private static bool HasValue(YamlNode value) => value switch
    {
        YamlMapping map when EntityCatalog.IsEntry(map) => map["value"] is { } inner && !IsUnset(inner),
        YamlSequence { Items.Count: > 0 } list when list.Items.All(EntityCatalog.IsEntry) =>
            list.Items.Cast<YamlMapping>().Any(m => m["value"] is { } inner && !IsUnset(inner)),
        _ => !IsUnset(value),
    };

    private static bool IsUnset(YamlNode value) => value is YamlScalar { IsNull: true } or YamlSequence { Items.Count: 0 };

    /// <summary>F-POS-4: a well-formed position that names a book or chapter the universe doesn't have.</summary>
    private static void CheckPositions(Universe universe, PositionScan scan, List<Diagnostic> found)
    {
        foreach (var reference in scan.References)
        {
            if (reference.Position is { } position && universe.Resolve(position).ToDiagnostic(reference.Line) is { } diagnostic)
            {
                var what = reference.Site == PositionSite.FieldValue ? "is kept, but not placed in story order" : "is left out of canon views";
                found.Add(diagnostic with { Message = $"{reference.Text}: {diagnostic.Message} Until it resolves, the fact that uses it {what}." });
            }
        }
    }

    /// <summary>F-TMP-3 (an entry whose <c>until</c> isn't after its <c>from</c>) and F-TMP-5 (live entries that overlap).</summary>
    private static void CheckEntries(EntityFacts facts, List<Diagnostic> found)
    {
        foreach (var field in facts.Fields)
        {
            foreach (var entry in field.Entries.Where(e => e.IsEmptyRange))
            {
                found.Add(new Diagnostic("F-TMP-3", DiagnosticSeverity.Warning, entry.Line,
                    $"{field.Name}: until {entry.UntilText} isn't after from {entry.FromText ?? "the start of the story"}, so this entry is never in effect."));
            }

            var live = field.Entries.Where(e => e.IsLive)
                .Select(e => new Span(e.EffectiveFrom!.Value, e.FromText, e.UntilText is null ? null : e.EffectiveUntil, e.UntilText, e.Line))
                .ToList();
            foreach (var (line, message) in Overlaps(live))
            {
                found.Add(new Diagnostic("F-TMP-5", DiagnosticSeverity.Warning, line,
                    $"{field.Name}: {message} Where they overlap, views use the entry with the later from, or the one later in the file."));
            }
        }
    }

    /// <summary>
    /// F-SEC-8 (a section whose <c>until</c> isn't after its <c>from</c>) and
    /// F-SEC-4 (live level-2 standard sections with the same name that overlap).
    /// </summary>
    private static void CheckSections(EntityFacts facts, List<Diagnostic> found)
    {
        foreach (var fact in facts.Sections.Where(f => f.IsEmptyRange))
        {
            found.Add(new Diagnostic("F-SEC-8", DiagnosticSeverity.Warning, fact.Section.Comment?.Line ?? fact.Section.Line,
                $"'{fact.Section.Name}': until {fact.UntilText} isn't after from {fact.FromText ?? fact.EffectiveFrom!.Description}, so this section is never in effect."));
        }

        var standard = facts.Sections.Where(f => f.IsStandard && f.IsLive)
            .GroupBy(f => LinkText.Normalize(f.Section.Name), StringComparer.Ordinal);
        foreach (var group in standard)
        {
            var spans = group
                .Select(f => new Span(f.EffectiveFrom!.Point, f.EffectiveFrom.Description, f.UntilText is null ? null : f.EffectiveUntil, f.UntilText,
                    f.Section.Comment?.Line ?? f.Section.Line))
                .ToList();
            foreach (var (line, message) in Overlaps(spans))
            {
                found.Add(new Diagnostic("F-SEC-4", DiagnosticSeverity.Warning, line,
                    $"'{group.First().Section.Name}': {message} Views show both where they overlap."));
            }
        }
    }

    /// <summary>A live fact's range, for the overlap rules.</summary>
    /// <param name="From">Where it starts.</param>
    /// <param name="FromText">Its from as written or described, or null for the start of the story.</param>
    /// <param name="Until">Its explicit until, or null if it has none.</param>
    /// <param name="UntilText">Its until as written.</param>
    /// <param name="Line">The line to report on.</param>
    private sealed record Span(StoryPoint From, string? FromText, StoryPoint? Until, string? UntilText, int Line);

    /// <summary>
    /// F-TMP-5, F-SEC-4: two live facts overlap if they share a from, or if
    /// an explicit until reaches past the next one's from. Each overlap is
    /// reported once, on the later fact for a shared from and on the one
    /// whose until runs on otherwise.
    /// </summary>
    private static IEnumerable<(int Line, string Message)> Overlaps(List<Span> spans)
    {
        var ordered = spans.OrderBy(s => s.From).ThenBy(s => s.Line).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].From.CompareTo(ordered[i - 1].From) == 0)
            {
                yield return (ordered[i].Line,
                    $"this and the one on line {ordered[i - 1].Line} both start at {ordered[i].FromText ?? "the start of the story"}, so they overlap.");
            }
        }

        foreach (var span in ordered.Where(s => s.Until is not null))
        {
            var next = ordered.FirstOrDefault(s => s.From > span.From);
            if (next is not null && span.Until!.Value > next.From)
            {
                yield return (span.Line,
                    $"this runs until {span.UntilText}, past the from of the one on line {next.Line} ({next.FromText}), so they overlap.");
            }
        }
    }
}
