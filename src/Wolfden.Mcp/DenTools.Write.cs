using System.Text.Json;
using System.Text.RegularExpressions;
using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.Editing;
using Wolfden.Core.Entities;
using Wolfden.Core.History;
using Wolfden.Core.Links;
using Wolfden.Core.Structure;
using Wolfden.Core.Types;
using Wolfden.Core.Yaml;
using Wolfden.Index;

namespace Wolfden.Mcp;

/// <summary>
/// The write tools (design.md §8, #21). Every write is checked as the den
/// would read it before anything is saved (<see cref="DenWriteCheck"/>), so
/// an agent can't save a file the parser would reject (format.md §1.3).
/// Writes go to the den's files only, through the den store; nothing is
/// committed until <see cref="Checkpoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stale files.</b> Every edit of an existing file takes the hash of the
/// content it was based on (<c>expected_hash</c>, from <c>get_entity</c> or
/// <c>get_chapter</c>, or an earlier write's result) and refuses the write
/// if the file has changed since (maintainer decision, 2026-10-23).
/// </para>
/// <para>
/// <b>Frozen books.</b> Nothing in a final book's scope is written: its
/// <c>book.md</c>, outline, or entities (F-BOOK-1). Pinned canon can be
/// edited, but the result lists every pinned fact the edit touched
/// (maintainer decision, 2026-10-22).
/// </para>
/// <para>
/// <b>Ids.</b> An id a write puts on disk is checked against every id in
/// the den, through the index, even in a session pinned to one universe
/// (maintainer decision, 2026-10-23). An existing id never changes (F-ID-4).
/// </para>
/// <para>
/// Writes are made one at a time: a lock keeps two calls from reading the
/// same file and saving over each other.
/// </para>
/// </remarks>
public sealed partial class DenTools
{
    private readonly Lock _writeLock = new();

    /// <summary>
    /// The den files this session's writes saved since a checkpoint last
    /// recorded them, each with the bytes the session last wrote, so
    /// <see cref="Checkpoint"/> can tell the session's edits from changes
    /// made some other way. A file a failed write put back as it was isn't
    /// here. Paths compare as the file system does (ignoring case on
    /// Windows and macOS, as git's paths may differ in case there).
    /// A null value marks a file this session deleted: the old path of a
    /// file <c>move_entity</c> moved. Guarded by <see cref="_writeLock"/>.
    /// </summary>
    private readonly Dictionary<string, byte[]?> _sessionWritten =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private IDenStore? _store;

    /// <summary>The den store writes go through; by default the den's folder on disk.</summary>
    public IDenStore Store
    {
        get => _store ??= new FileSystemDenStore(Root);
        init => _store = value;
    }

    /// <summary>Makes new ids; tests replace it to make a clash.</summary>
    internal Func<string> NewId { get; init; } = Ids.New;

    /// <summary>
    /// Creates an entity from its type's template (types.md F-TPL-6) in the
    /// universe, or in one of its series or books, with a new id that no
    /// file in the den uses. The slug is made from the name unless one is
    /// given; a slug made from the name that's taken gets <c>-2</c>, <c>-3</c>,
    /// and so on, but one asked for that's taken is refused.
    /// </summary>
    public WriteResult CreateEntity(
        string type, string name, string? universe = null, string? series = null, string? book = null, string? slug = null,
        IReadOnlyList<string>? aliases = null, string? status = null, string? from = null, JsonElement? fields = null, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the type in 'type', such as 'character'.");
        }

        CheckName(name);
        var fieldValues = fields is { } f ? FieldValues(f) : [];
        return Create(type.Trim(), name, universe, series, book, slug, aliases, status, from, fieldValues, text);
    }

    /// <summary>
    /// Records undecided material with <c>status: idea</c> (format.md §9.3).
    /// With <paramref name="entity"/>, it adds a new section to that entity,
    /// marked <c>status=idea</c>. Without it, it creates an entity of
    /// <paramref name="type"/> (by default <c>idea</c>, types.md §9.1) with
    /// <c>status: idea</c>, the text in its lead.
    /// </summary>
    public WriteResult AddIdea(
        string text, string? entity = null, string? section = null, string? expectedHash = null, string? name = null, string? type = null,
        string? universe = null, string? series = null, string? book = null, JsonElement? fields = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the idea in 'text'.");
        }

        if (entity is not null)
        {
            if (name is not null || type is not null || series is not null || book is not null || fields is not null)
            {
                throw new DenToolException(
                    ToolErrorCodes.BadArgument,
                    "With 'entity', the idea is a section of that entity: 'name', 'type', 'series', 'book', and 'fields' are for a new idea entity. Leave them out, or leave out 'entity'.");
            }

            return AddSection(entity, section ?? "Idea", text, RequireHash(expectedHash), universe, null, null, newSection: true, null, null, "idea", null);
        }

        if (section is not null || expectedHash is not null)
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument, "'section' and 'expected_hash' are for an idea added to an existing entity: give 'entity' too, or leave them out.");
        }

        if (name is null)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the new idea a name in 'name', or name an existing entity in 'entity' to add the idea to it.");
        }

        CheckName(name);
        var fieldValues = fields is { } f ? FieldValues(f) : [];
        return Create((type ?? "idea").Trim(), name, universe, series, book, null, null, "idea", null, fieldValues, text);
    }

    /// <summary>
    /// Changes an entity's frontmatter: <c>set</c> a value, <c>add</c> an
    /// item to a list (an alias, or an entry of a field that changes over
    /// time), or <c>remove</c> a key or item, each at a path such as
    /// <c>fields.rank.1.from</c>. The changes are made in order, and saved
    /// together or not at all. <c>id</c> never changes (F-ID-4); the
    /// <c>name</c>, and existing aliases, are changed by
    /// <see cref="RenameEntity"/> and <see cref="ChangeAlias"/>, since links
    /// that matched the old text have to follow (F-RT-5).
    /// </summary>
    public WriteResult UpdateFields(string entity, string expectedHash, JsonElement changes, string? universe = null)
    {
        var hash = RequireHash(expectedHash);
        var ops = Changes(changes, chapter: false);
        return EditEntity(entity, universe, hash, (file, catalog, pinned) =>
        {
            var touched = new List<string>();
            var added = new List<string>();
            var notes = new List<string>();
            var facts = EntityFacts.For(catalog, new Bookkeeping(catalog.Universe.Den.BookkeepingSections));
            var held = facts.Fields.SelectMany(f => f.Entries.Where(e => e.Pinned).Select(e => $"fields.{f.Name}"))
                .Concat(facts.Sections.Where(s => s.Pinned && (s.Section.Parent is null || !facts[s.Section.Parent].Pinned)).Select(s => $"section '{s.Section.Name}'"))
                .Distinct().ToList();
            foreach (var op in ops)
            {
                var path = op.Path!;
                CheckFrontmatterPath(path, op.Op);
                touched.Add(path.Count > 1 && path[0] == "fields" ? path[1] : path[0]);
                pinned.AddRange(PinnedTouched(file, path, op.Op, op.Value));
                if (path[0] is "status" or "from" or "type" && held.Count > 0)
                {
                    // Its pinned facts take the entity's status (F-STAT-3), start no earlier than it does, and are read by its type.
                    pinned.Add($"'{path[0]}' changes the entity that holds pinned canon ({string.Join(", ", held)}), so it changes how those facts are read");
                }

                if (path[0] == "aliases")
                {
                    if (op.RawValue is not { ValueKind: JsonValueKind.String } alias || string.IsNullOrWhiteSpace(alias.GetString()) || alias.GetString()!.Any(char.IsControl))
                    {
                        throw new DenToolException(ToolErrorCodes.BadArgument, "An alias is one line of text, not empty (F-ENT-4).");
                    }

                    added.Add(alias.GetString()!.Trim());
                }

                file = Apply(op.Op switch
                {
                    "set" => () => file.WithFrontmatterValue(path, op.Value!, inTemplateOrder: true),
                    "add" => () => file.WithNewListItem(path, op.Value!, op.Index, inTemplateOrder: true),
                    _ => () => Remove(file, path, notes),
                });
            }

            return (file, (Func<CatalogEntity, CatalogEntity, IEnumerable<string>>)((before, after) =>
                FieldEffects(before, after, touched).Concat(added.SelectMany(a => AliasEffects(after, a))).Concat(notes).ToList()));
        });
    }

    /// <summary>
    /// Adds text to the end of a section of an entity, after what's there, or
    /// adds the section if the entity doesn't have one of that name (or
    /// <paramref name="newSection"/> asks for another, such as a new
    /// <c>## Appearance</c> from a later chapter). A new section can have a
    /// position and a status, written as its section comment (format.md
    /// §9.2), and goes where types.md F-TPL-8 says: a standard section before
    /// the first one after it in the type's order, a custom one at the end,
    /// and a subsection (<paramref name="under"/>) at the end of its parent.
    /// </summary>
    public WriteResult AppendSection(
        string entity, string section, string text, string expectedHash, string? universe = null, string? under = null, int? sectionLine = null,
        bool newSection = false, string? from = null, string? until = null, string? status = null, bool? pin = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the text to add in 'text'.");
        }

        return AddSection(entity, section, text, RequireHash(expectedHash), universe, under, sectionLine, newSection, from, until, status, pin);
    }

    /// <summary>
    /// Renames a section of an entity, and rewrites every link to it by
    /// heading (<c>[[entity#Old name]]</c>) in any file of the den, in the
    /// same change (format.md F-RT-5). If any file that links to it is in a
    /// final book, nothing is renamed.
    /// </summary>
    public WriteResult RenameSection(string entity, string section, string newName, string expectedHash, string? universe = null, int? sectionLine = null)
    {
        var hash = RequireHash(expectedHash);
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(newName))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the section's name in 'section' and its new name in 'new_name'.");
        }

        newName = newName.Trim();
        if (newName.Any(char.IsControl))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "A section name is one line of text.");
        }

        var (path, _) = Locate(entity, universe);
        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var catalog = EntityCatalog.Load(structure, den);
            var target = catalog.FindByPath(path) ?? throw (den.FileExists(path)
                ? new DenToolException(
                    ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be edited here. Call check with path '{path}' to see them.")
                : Gone(path));
            CheckHash(path, target.File.ToBytes(), hash);
            CheckNotFrozen(target);
            var found = FindSection(target.File, section, null, sectionLine);
            var facts = EntityFacts.For(target, new Bookkeeping(structure.BookkeepingSections));
            var pinned = facts[found].Pinned ? new List<string> { $"section '{found.Name}' (line {found.Line}) is pinned canon" } : [];
            if (found.Name == newName)
            {
                return Unchanged(path, target.File.ToBytes(), target);
            }

            var links = LinkIndex.Build(catalog, den);
            SectionRenamePlan plan;
            try
            {
                plan = SectionRename.Plan(links, den, target, found, newName);
            }
            catch (ArgumentException e)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, e.Message);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or EditVerificationException)
            {
                throw new DenToolException(ToolErrorCodes.UnsupportedEdit, $"The rename can't be made in place: {e.Message}");
            }

            if (plan.FrozenPaths.Count > 0)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen,
                    $"Links to '{found.Name}' are in files of a final (frozen) book, which can't be edited until it's unfrozen (F-BOOK-1): " +
                    $"{string.Join(", ", plan.FrozenPaths)}. Renaming the section would break them, so nothing was changed.");
            }

            var files = plan.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var idAdded = AddMissingId(target, files);
            var check = Check(den, structure, files);
            Save(files, plan.Originals, created: null);
            var after = EntityCatalog.ReadEntityFile(check.Structure, path, files[path]).Entity!;
            var effects = new List<string>
            {
                plan.LinksRewritten == 0
                    ? $"Renamed '{found.Name}' to '{newName}'. No links named it."
                    : $"Renamed '{found.Name}' to '{newName}', and rewrote {plan.LinksRewritten} link(s) to it so they name '{newName}' (files written: {files.Count}).",
            };
            if (target.File.Sections.FirstOrDefault(s => s.Index < found.Index && LinkText.Matches(s.Name, newName)) is { } earlier)
            {
                effects.Add($"An earlier section, '{earlier.Name}' at line {earlier.Line}, already has that name, so a link to '{newName}' reaches that one first (F-LINK-9).");
            }

            if (plan.SkippedPaths.Count > 0)
            {
                effects.Add(
                    $"These files have errors, so the den leaves them out and they weren't edited, but their text links to '{found.Name}': " +
                    $"{string.Join(", ", plan.SkippedPaths)}. Once they're fixed, change those links to '{newName}' by hand.");
            }

            if (target.Definition is { } type && type.IsStandardSection(found.Name) != type.IsStandardSection(newName))
            {
                effects.Add(type.IsStandardSection(newName)
                    ? $"'{newName}' is one of the standard sections of type '{type.Key}', so it now hands over to a later section of the same name (F-SEC-8)."
                    : $"'{newName}' isn't a standard section of type '{type.Key}', so it's now a custom section, which never hands over to a later one (F-SEC-8).");
            }

            return new WriteResult(
                ToolJson.Version, true, path, HashOf(files[path]), Summary(after), effects, pinned, Warnings(check),
                files.OrderBy(p => p.Key == path ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new WrittenFileView(p.Key, HashOf(p.Value))).ToList(),
                IdAdded: idAdded);
        }
    }

    /// <summary>
    /// A book's chapters, or one chapter, with each chapter field's own value,
    /// its effective value, and where that comes from (types.md F-CHAP-5),
    /// and the hash of <c>book.md</c> for <see cref="UpdateChapterFields"/>.
    /// </summary>
    public GetChapterResult GetChapter(string book, string? chapter = null, string? universe = null)
    {
        if (string.IsNullOrWhiteSpace(book))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the book's key in 'book'.");
        }

        var (universeKey, _) = LocateBook(book.Trim(), universe);
        var structure = DenStructure.Load(Snapshot());
        var found = FindBook(structure, universeKey, book.Trim());
        var chapters = chapter is null ? found.Chapters.ToList() : [FindChapter(found, chapter)];
        return new GetChapterResult(
            ToolJson.Version, universeKey, found.Key, found.FilePath, HashOf(found.File.ToBytes()), found.Status == BookStatus.Final,
            chapters.Count, chapters.Select(ChapterOf).ToList());
    }

    /// <summary>
    /// Changes a chapter's own fields in <c>book.md</c> (types.md §8):
    /// <c>set</c> a value, <c>add</c> an item to a list field, <c>remove</c>
    /// an item (the last one leaves <c>[]</c>, "none here", which stops
    /// inheritance, format.md F-RT-6), or <c>clear</c> the field, so the
    /// chapter goes back to inheriting from <c>chapter_defaults</c>. The
    /// result says what each field's effective value is now.
    /// </summary>
    public WriteResult UpdateChapterFields(string book, string chapter, string expectedHash, JsonElement changes, string? universe = null)
    {
        var hash = RequireHash(expectedHash);
        if (string.IsNullOrWhiteSpace(book) || string.IsNullOrWhiteSpace(chapter))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the book's key in 'book' and the chapter's key in 'chapter'.");
        }

        var ops = Changes(changes, chapter: true);
        var (universeKey, _) = LocateBook(book.Trim(), universe);
        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var target = FindBook(structure, universeKey, book.Trim());
            var ch = FindChapter(target, chapter);
            var path = target.FilePath;
            var original = target.File.ToBytes();
            CheckHash(path, original, hash);
            if (target.Status == BookStatus.Final)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen, $"Book '{target.Key}' is final (frozen), so its book.md can't be edited until it's unfrozen (F-BOOK-1).");
            }

            var file = target.File;
            var items = (file.GetFrontmatterValue("chapters") as YamlSequence)?.Items ?? [];
            var index = Enumerable.Range(0, items.Count).Single(i => items[i] switch
            {
                YamlMapping m => m["key"] is YamlScalar { IsNull: false } k && k.Text == ch.Key,
                YamlScalar s => s.Text == ch.Key,
                _ => false,
            });
            var itemPath = new[] { "chapters", index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (items[index] is not YamlMapping)
            {
                throw new DenToolException(
                    ToolErrorCodes.UnsupportedEdit,
                    $"Chapter '{ch.Key}' is written as a bare key in book.md, so it has no place for fields. Write it as '{{ key: {ch.Key} }}' first.");
            }

            var touched = new List<string>();
            var notes = new List<string>();
            foreach (var op in ops)
            {
                var field = op.Field!;
                touched.Add(field);
                string[] fieldPath = [.. itemPath, "fields", field];
                file = Apply(op.Op switch
                {
                    "set" => () => file.WithFrontmatterValue(fieldPath, op.Value!),
                    "add" => () => file.WithNewListItem(fieldPath, op.Value!, op.Index),
                    "remove" => () => NoteEmptied(
                        notes, file.WithoutChapterFieldItem(ch.Key, field, ItemToRemove(file, fieldPath, op), out var comments), comments, $"{field} of chapter '{ch.Key}'"),
                    _ => () => Clear(file, fieldPath, ch, field, notes),
                });
            }

            if (file.ToBytes().AsSpan().SequenceEqual(original))
            {
                // A write that changes nothing never adds an id (F-ID-6).
                return new WriteResult(
                    ToolJson.Version, false, path, HashOf(original), null, ["Nothing changed: book.md already says that."], [], [],
                    Chapter: ChapterOf(ch));
            }

            var idAdded = target.Id is null ? AddId(ref file) : null;
            var bytes = file.ToBytes();

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = bytes };
            var check = Check(den, structure, files);
            Save(files, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = original }, created: null);
            var afterBook = FindBook(check.Structure, universeKey, target.Key);
            var afterChapter = afterBook.FindChapter(ch.Key)!;
            return new WriteResult(
                ToolJson.Version, true, path, HashOf(bytes), null, [.. ChapterEffects(ch, afterChapter, touched.Distinct().ToList()), .. notes], [], Warnings(check),
                Chapter: ChapterOf(afterChapter), IdAdded: idAdded);
        }
    }

    /// <summary>
    /// Records the den's files as they are now as a checkpoint (a git commit)
    /// with <paramref name="message"/>, naming <paramref name="coAuthor"/>,
    /// the agent, as a co-author (design.md §6). Only den files are
    /// recorded; anything else the writer staged stays as it was. A den
    /// without history isn't an error: the result says why there's no
    /// checkpoint, and the edits are still saved in the files.
    /// </summary>
    public CheckpointResult Checkpoint(string message, string coAuthor, IReadOnlyList<string>? paths = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the checkpoint a message in 'message', such as 'End of chapter 14 session'.");
        }

        if (message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "A checkpoint's message can't hold control characters other than line breaks and tabs.");
        }

        if (string.IsNullOrWhiteSpace(coAuthor) || coAuthor.Any(char.IsControl) || !CoAuthorPattern().IsMatch(coAuthor.Trim()))
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument,
                "Give yourself as the co-author in 'co_author', as 'Name <email>' on one line, such as 'Claude <noreply@anthropic.com>'; the checkpoint records you with a Co-Authored-By line.");
        }

        var given = paths?.Select(p => (p ?? "").Replace('\\', '/').Trim().Trim('/')).ToList();
        if (given is not null && (given.Count == 0 || given.Any(p => p.Length == 0)))
        {
            // An empty list mustn't quietly become the whole den.
            throw new DenToolException(
                ToolErrorCodes.BadArgument, "'paths' names files or folders relative to the den root, none empty. Leave it out to checkpoint the whole den.");
        }

        lock (_writeLock)
        {
            var history = History();
            try
            {
                // Before recording: forget the session's writes that are recorded already (the
                // writer committed them elsewhere), and note what each tracked file holds now,
                // which is what the checkpoint records.
                var onDisk = new Dictionary<string, byte[]?>(_sessionWritten.Comparer);
                foreach (var (path, bytes) in _sessionWritten.ToList())
                {
                    if (bytes is null)
                    {
                        // Deleted by this session. A file there again was put there some other way,
                        // so the deletion isn't the session's to claim any more.
                        onDisk[path] = Store.TryReadFile(path);
                        if (onDisk[path] is not null)
                        {
                            _sessionWritten.Remove(path);
                        }
                    }
                    else if (history.HoldsAtLatest(path, bytes) is { IsAvailable: true, Value: true })
                    {
                        _sessionWritten.Remove(path);
                    }
                    else
                    {
                        onDisk[path] = Store.TryReadFile(path);
                    }
                }

                var result = history.Checkpoint(new CheckpointRequest(message.Trim(), given, [coAuthor.Trim()]));
                if (!result.IsAvailable)
                {
                    return new CheckpointResult(ToolJson.Version, history.Name, false, result.Reason, false, null);
                }

                if (result.Value is not { } e)
                {
                    return new CheckpointResult(ToolJson.Version, history.Name, true, null, false, null);
                }

                // Which of the recorded files this session's writes saved, and which changed some
                // other way (the writer's editor, another tool), so the agent can tell the writer
                // (maintainer decision, 2026-10-25). A file the session wrote and something else
                // changed after is in both lists. Committed files start afresh.
                var ours = new List<string>();
                var theirs = new List<string>();
                foreach (var change in e.Changes)
                {
                    var tracked = _sessionWritten.ContainsKey(change.Path) ? change.Path
                        : change.OldPath is { } was && _sessionWritten.ContainsKey(was) ? was : null;
                    if (tracked is null)
                    {
                        theirs.Add(change.Path);
                        continue;
                    }

                    ours.Add(change.Path);
                    var now = change.Path == tracked ? onDisk.GetValueOrDefault(tracked) : Store.TryReadFile(change.Path);
                    var wrote = _sessionWritten[tracked];
                    if (wrote is null ? now is not null : now is null || !now.AsSpan().SequenceEqual(wrote))
                    {
                        theirs.Add(change.Path);
                    }
                }

                foreach (var change in e.Changes)
                {
                    _sessionWritten.Remove(change.Path);
                    if (change.OldPath is { } old)
                    {
                        _sessionWritten.Remove(old);
                    }
                }

                return new CheckpointResult(
                    ToolJson.Version, history.Name, true, null, true,
                    new HistoryEntryView(e.Id, e.When, e.Author, e.Message, e.Changes.Select(c => new HistoryChangeView(c.Kind.ToString().ToLowerInvariant(), c.Path, c.OldPath)).ToList()),
                    ours.Order(StringComparer.Ordinal).ToList(),
                    theirs.Order(StringComparer.Ordinal).ToList());
            }
            catch (ArgumentException e)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, e.Message);
            }
            catch (HistoryException e)
            {
                throw new DenToolException(ToolErrorCodes.HistoryFailed, e.Message);
            }
        }
    }

    // ---------------------------------------------------------------- create

    private WriteResult Create(
        string typeKey, string name, string? universe, string? series, string? book, string? slug, IReadOnlyList<string>? aliases, string? status,
        string? from, IReadOnlyList<KeyValuePair<string, YamlValue>> fields, string? text)
    {
        var filter = Filter(series, book);
        var (universeKey, ids) = WithIndex(index =>
        {
            var db = index.Context;
            var u = ChooseUniverse(db, universe, null, allowAll: false)!;
            CheckFilter(db, u, filter);

            // Ids are compared across the whole den, whatever universe the session reads.
            var used = db.Entities.Where(e => e.Ulid != null).Select(e => e.Ulid!).ToList()
                .Concat(db.Containers.Where(c => c.Ulid != null).Select(c => c.Ulid!))
                .ToHashSet(StringComparer.Ordinal);
            return (u.Key, used);
        });

        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var u = structure.FindUniverse(universeKey) ?? throw Gone($"universes/{universeKey}");
            Container scope = filter is null ? u
                : filter.Kind == CanonFilterKind.Series ? u.Series.Single(s => s.Key == filter.Key)
                : u.FindBook(filter.Key) ?? throw new DenToolException(ToolErrorCodes.UnknownScope, $"Universe '{u.Key}' has no book '{filter.Key}'.");
            if (scope is Book { Status: BookStatus.Final } frozen)
            {
                throw new DenToolException(
                    ToolErrorCodes.Frozen, $"Book '{frozen.Key}' is final (frozen), so nothing can be added to it until it's unfrozen (F-BOOK-1).");
            }

            var type = structure.Types.Find(typeKey) ?? throw new DenToolException(
                ToolErrorCodes.UnknownType,
                $"No enabled pack defines the type '{typeKey}', so there's no template to make it from. Enabled types: " +
                $"{string.Join(", ", structure.Types.Types.Select(t => t.Key).Order(StringComparer.Ordinal))}.");
            var id = NewUnusedId(ids);
            NewEntityFile made;
            try
            {
                made = NewEntity.Create(den, scope, type, id, new NewEntityRequest(name.Trim())
                {
                    Slug = slug?.Trim(), Aliases = aliases, Status = status?.Trim(), From = from?.Trim(), Fields = fields,
                });
            }
            catch (ArgumentException e) when (e.Message.Contains("is taken", StringComparison.Ordinal))
            {
                throw new DenToolException(ToolErrorCodes.AlreadyExists, e.Message.Split(" (Parameter", 2)[0]);
            }
            catch (ArgumentException e)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, e.Message.Split(" (Parameter", 2)[0]);
            }
            catch (EditVerificationException e)
            {
                throw new DenToolException(ToolErrorCodes.UnsupportedEdit, e.Message);
            }

            var file = made.File;
            if (!string.IsNullOrWhiteSpace(text))
            {
                file = Apply(() => file.WithTextAddedToLead(text));
            }

            var bytes = file.ToBytes();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [made.Path] = bytes };
            var check = Check(den, structure, files);
            Save(files, null, created: made.Path);
            var entity = EntityCatalog.ReadEntityFile(check.Structure, made.Path, bytes).Entity!;
            var effects = new List<string> { $"Created {made.Path} from the '{type.Key}' template, with id {id}." };
            if (!entity.Status.IsInCanon())
            {
                effects.Add($"Its status is '{entity.Status.ToKey()}', so canon views leave it out until the writer settles it (format.md §9.3).");
            }

            if (entity.From is null)
            {
                effects.Add($"It has no 'from', so it enters canon at {scope.StartDescription} (F-ENT-6).");
            }

            return new WriteResult(
                ToolJson.Version, true, made.Path, HashOf(bytes), Summary(entity), effects, [], Warnings(check),
                [new WrittenFileView(made.Path, HashOf(bytes), Created: true)]);
        }
    }

    private string NewUnusedId(HashSet<string> used)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = NewId();
            if (!used.Contains(id))
            {
                return id;
            }
        }

        throw new DenToolException(ToolErrorCodes.AlreadyExists, "Every new id tried is already used in the den, so no id could be given. Try again.");
    }

    // ------------------------------------------------------------- sections

    private WriteResult AddSection(
        string entity, string section, string text, string hash, string? universe, string? under, int? sectionLine, bool newSection,
        string? from, string? until, string? status, bool? pin)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the section's name in 'section'.");
        }

        section = section.Trim();
        if (section.Any(char.IsControl))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "A section name is one line of text.");
        }

        var attributes = new List<string>();
        foreach (var (key, value) in new[] { ("status", status), ("from", from), ("until", until), ("pin", pin is null ? null : pin.Value ? "true" : "false") })
        {
            if (value is null)
            {
                continue;
            }

            var v = value.Trim();
            if (v.Length == 0 || v.Any(c => char.IsWhiteSpace(c) || c == '"') || v.Contains("-->", StringComparison.Ordinal))
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"'{key}' must be one word, such as a position 'dracula:5' or a status 'idea'.");
            }

            attributes.Add($"{key}={v}");
        }

        return EditEntity(entity, universe, hash, (file, catalog, pinned) =>
        {
            var parent = under is null ? null : FindSection(file, under, null, null);
            var candidates = parent is null ? file.Sections.ToList() : parent.Subsections.ToList();
            var matches = candidates.Where(s => LinkText.Matches(s.Name, section) && (sectionLine is null || s.Line == sectionLine)).ToList();
            var facts = EntityFacts.For(catalog, new Bookkeeping(catalog.Universe.Den.BookkeepingSections));
            var effects = new List<string>();
            if (!newSection && matches.Count > 0)
            {
                if (matches.Count > 1)
                {
                    throw AmbiguousSection(section, matches);
                }

                if (attributes.Count > 0)
                {
                    throw new DenToolException(
                        ToolErrorCodes.BadArgument,
                        $"'{matches[0].Name}' is already there, and its status and positions aren't changed here. To add another '{section}' with these, pass new_section: true.");
                }

                var target = matches[0];
                if (facts[target].Pinned)
                {
                    pinned.Add($"section '{target.Name}' (line {target.Line}) is pinned canon");
                }

                var body = target.Body;
                var joined = string.IsNullOrWhiteSpace(body) ? text : body.TrimEnd() + "\n\n" + text;
                file = Apply(() => file.WithSectionBody(target, joined));
                effects.Add($"Added the text at the end of '{target.Name}' (line {target.Line}).");
            }
            else
            {
                if (sectionLine is not null && !newSection)
                {
                    throw new DenToolException(ToolErrorCodes.UnknownSection, $"No section '{section}' starts at line {sectionLine}.");
                }

                var level = parent is null ? 2 : parent.Level + 1;
                if (level > 6)
                {
                    throw new DenToolException(ToolErrorCodes.UnsupportedEdit, $"'{parent!.Name}' is a level-6 heading, so it can't hold a subsection.");
                }

                var before = parent is null ? StandardPlace(file, catalog.Definition, section) : file.Sections.FirstOrDefault(s => s.Index > parent.Index && s.Level <= parent.Level);
                var comment = attributes.Count == 0 ? null : string.Join(' ', attributes);
                file = Apply(() => file.WithNewSection(section, text, level, before, comment));
                if (pin == true || (parent is not null && facts[parent].Pinned))
                {
                    pinned.Add(pin == true ? $"new section '{section}' is pinned canon" : $"new section '{section}' is under the pinned section '{parent!.Name}', so it's pinned canon too (F-PIN-2)");
                }

                effects.Add(parent is null ? $"Added the section '{section}'." : $"Added '{section}' as a subsection of '{parent.Name}'.");
                if (status is not null && FactStatuses.Parse(status.Trim()) is { } s && !s.IsInCanon())
                {
                    effects.Add($"It's marked '{s.ToKey()}', so canon views leave it out (format.md §9.3).");
                }
                else if (!catalog.Status.IsInCanon())
                {
                    effects.Add($"The entity is '{catalog.Status.ToKey()}', so canon views leave the section out with it (F-STAT-3).");
                }

                if (parent is null && catalog.Definition is { } type && type.IsStandardSection(section)
                    && file.TopLevelSections.Count(t => LinkText.Matches(t.Name, section)) > 1)
                {
                    effects.Add(from is null
                        ? $"'{section}' is a standard section of type '{type.Key}' and the entity already has one. Without a 'from', the new one starts where the entity does, as the earlier one does, so they overlap (F-SEC-4); give it the 'from' where it takes over."
                        : $"'{section}' is a standard section of type '{type.Key}', so from {from} the new one takes over from the earlier one (F-SEC-8).");
                }
            }

            return (file, (Func<CatalogEntity, CatalogEntity, IEnumerable<string>>)((_, _) => effects));
        });
    }

    /// <summary>Where a new level-2 section goes (types.md F-TPL-8): a standard section before the first level-2 section later in the type's order, anything else at the end.</summary>
    private static Section? StandardPlace(MarkdownFile file, TypeDefinition? type, string name)
    {
        if (type?.FindSection(name) is not { } definition)
        {
            return null;
        }

        var order = type.Sections.ToList();
        var rank = order.IndexOf(definition);
        return file.TopLevelSections.FirstOrDefault(s => type.FindSection(s.Name) is { } d && order.IndexOf(d) > rank);
    }

    private static Section FindSection(MarkdownFile file, string name, Section? parent, int? line)
    {
        var candidates = parent is null ? file.Sections : parent.Subsections;
        var matches = candidates.Where(s => LinkText.Matches(s.Name, name.Trim()) && (line is null || s.Line == line)).ToList();
        return matches.Count switch
        {
            0 => throw new DenToolException(
                ToolErrorCodes.UnknownSection,
                $"The entity has no section '{name}'{(line is null ? "" : $" at line {line}")}. Its sections: " +
                (file.Sections.Count == 0 ? "none." : string.Join(", ", file.Sections.Select(s => $"'{s.Name}' (line {s.Line})")) + ".")),
            1 => matches[0],
            _ => throw AmbiguousSection(name, matches),
        };
    }

    private static DenToolException AmbiguousSection(string name, IReadOnlyList<Section> matches) => new(
        ToolErrorCodes.AmbiguousSection,
        $"The entity has {matches.Count} sections named '{name}': " +
        string.Join(", ", matches.Select(s => $"line {s.Line}{(s.Comment?["from"] is { } f ? $" (from {f})" : "")}")) +
        ". Ask again with 'section_line' set to the line of the one you mean.");

    // ------------------------------------------------------------ the edit loop

    /// <summary>
    /// Finds the entity, checks its hash and that it isn't frozen, makes the
    /// edit, adds a missing id (F-ID-6), checks the result as the den would
    /// read it, and saves it.
    /// </summary>
    private WriteResult EditEntity(
        string entity, string? universe, string hash,
        Func<MarkdownFile, CatalogEntity, List<string>, (MarkdownFile File, Func<CatalogEntity, CatalogEntity, IEnumerable<string>> Effects)> edit)
    {
        var (path, ids) = Locate(entity, universe);
        lock (_writeLock)
        {
            var den = Snapshot();
            var structure = DenStructure.Load(den);
            var original = den.TryReadFile(path) ?? throw Gone(path);
            CheckHash(path, original, hash);
            var read = EntityCatalog.ReadEntityFile(structure, path, original);
            var before = read.Entity ?? throw new DenToolException(
                ToolErrorCodes.InvalidEdit, $"{path} has errors now, so the den leaves it out and it can't be edited here. Call check with path '{path}' to see them.");
            CheckNotFrozen(before);
            var pinned = new List<string>();
            var (file, effects) = edit(before.File, before, pinned);
            if (file.ToBytes().AsSpan().SequenceEqual(original))
            {
                // A write that changes nothing never adds an id (F-ID-6).
                return Unchanged(path, original, before);
            }

            var idAdded = before.Id is null ? AddId(ref file, ids) : null;
            var bytes = file.ToBytes();

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = bytes };
            var check = Check(den, structure, files);
            Save(files, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = original }, created: null);
            var after = EntityCatalog.ReadEntityFile(check.Structure, path, bytes).Entity!;
            var said = effects(before, after).ToList();
            if (idAdded is not null)
            {
                said.Add($"The file had no id, so it was given {idAdded} (F-ID-6).");
            }

            return new WriteResult(ToolJson.Version, true, path, HashOf(bytes), Summary(after), said, pinned.Distinct().ToList(), Warnings(check), IdAdded: idAdded);
        }
    }

    private static WriteResult Unchanged(string path, byte[] bytes, CatalogEntity entity) =>
        new(ToolJson.Version, false, path, HashOf(bytes), Summary(entity), ["Nothing changed: the file already says that."], [], []);

    /// <summary>The entity's path in the index, and every id the den uses (for a missing id, F-ID-6).</summary>
    private (string Path, HashSet<string> Ids) Locate(string entity, string? universe)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Name the entity in 'entity': its name, an alias, its file name, its id, or its path.");
        }

        return WithIndex(index =>
        {
            var db = index.Context;
            var universes = db.Containers.Where(c => c.Kind == "universe").ToDictionary(c => c.Id, c => c.Key);
            var scopes = Scopes(db);
            var u = ChooseUniverse(db, universe, null, allowAll: false)!;
            var matches = Match(index, u.Id, entity.Trim(), null);
            var path = matches.Count switch
            {
                0 => throw UnknownEntity(entity, u.Key),
                > 1 => throw Ambiguous(entity, matches, universes, scopes),
                _ => matches[0].Path,
            };
            var ids = db.Entities.Where(e => e.Ulid != null).Select(e => e.Ulid!).ToList()
                .Concat(db.Containers.Where(c => c.Ulid != null).Select(c => c.Ulid!))
                .ToHashSet(StringComparer.Ordinal);
            return (path, ids);
        });
    }

    private (string Universe, string Path) LocateBook(string book, string? universe) => WithIndex(index =>
    {
        var db = index.Context;
        var u = ChooseUniverse(db, universe, null, allowAll: false)!;
        CheckFilter(db, u, CanonFilter.ForBook(book));
        return (u.Key, db.Containers.Single(c => c.UniverseId == u.Id && c.Kind == "book" && c.Key == book).Path);
    });

    private static Book FindBook(DenStructure structure, string universe, string book) =>
        structure.FindUniverse(universe)?.FindBook(book)
        ?? throw new DenToolException(ToolErrorCodes.UnknownScope, $"Universe '{universe}' has no book '{book}' now; it may have changed since it was read.");

    private static Chapter FindChapter(Book book, string key)
    {
        key = key.Trim();
        return book.FindChapter(key) ?? throw new DenToolException(
            ToolErrorCodes.UnknownChapter,
            $"Book '{book.Key}' has no chapter '{key}'." + (book.Chapters.Count == 0 ? " It has no chapters yet." : $" Its chapters: {string.Join(", ", book.Chapters.Select(c => c.Key))}."));
    }

    private IDenSource Snapshot() => Store is FileSystemDenStore disk ? disk.Snapshot() : Store;

    private static string RequireHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new DenToolException(
                ToolErrorCodes.BadArgument,
                "Give 'expected_hash': the hash of the file your edit is based on, from get_entity (or get_chapter for book.md, or get_chapter_defaults for a universe.md, series.md, or book.md), or from your last write's result. The write is refused if the file has changed since.");
        }

        return hash.Trim().ToLowerInvariant();
    }

    private static void CheckHash(string path, byte[] bytes, string expected)
    {
        var actual = HashOf(bytes);
        if (actual != expected)
        {
            throw new DenToolException(
                ToolErrorCodes.StaleFile,
                $"{path} has changed since it was read (its hash is now {actual}, not {expected}), perhaps edited in another program. " +
                "Read it again, check that your edit still fits, and pass the new hash.");
        }
    }

    private static void CheckNotFrozen(CatalogEntity entity)
    {
        if (entity.Scope is Book { Status: BookStatus.Final } book)
        {
            throw new DenToolException(
                ToolErrorCodes.Frozen,
                $"{entity.Path} is in book '{book.Key}', which is final (frozen), so it can't be edited until the book is unfrozen (F-BOOK-1).");
        }

        // A backstop: `status: final` on an entity is an F-ENT-5 error, so such a
        // file never loads and never gets here. It's kept so that if the format
        // ever lets an entity be final on its own, it's refused, not edited.
        if (entity.File.Frontmatter?["status"] is YamlScalar { Text: "final" })
        {
            throw new DenToolException(ToolErrorCodes.Frozen, $"{entity.Path} is marked final, so it can't be edited.");
        }
    }

    private static DenToolException Gone(string path) =>
        new(ToolErrorCodes.StaleFile, $"{path} isn't in the den now; it may have been moved or deleted since it was read. Look it up again.");

    /// <summary>Checks the files as the den would read them, refusing any that would have an error.</summary>
    private static DenWriteCheckResult Check(IDenSource den, DenStructure structure, Dictionary<string, byte[]> files)
    {
        var check = DenWriteCheck.Check(den, structure, files);
        if (check.HasErrors)
        {
            throw new DenToolException(
                ToolErrorCodes.InvalidEdit,
                "The edit would leave errors, so the den would leave the file out, and nothing was written: " +
                string.Join("; ", check.Errors.Select(d => $"{d.Path}:{d.Diagnostic.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}: {d.Diagnostic.RuleId}: {d.Diagnostic.Message}")));
        }

        return check;
    }

    /// <summary>
    /// Writes the files, after checking that none changed since it was read
    /// (<paramref name="originals"/>), and that a new file isn't there yet.
    /// </summary>
    private void Save(Dictionary<string, byte[]> files, IReadOnlyDictionary<string, byte[]>? originals, string? created)
    {
        foreach (var (path, original) in originals ?? new Dictionary<string, byte[]>())
        {
            var now = Store.TryReadFile(path);
            if (now is null || !now.AsSpan().SequenceEqual(original))
            {
                throw new DenToolException(
                    ToolErrorCodes.StaleFile, $"{path} changed while the edit was being made, so nothing was written. Read it again and retry.");
            }
        }

        var written = new List<string>();
        try
        {
            foreach (var (path, bytes) in files.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Store.WriteFile(path, bytes, path == created ? DenWriteMode.Create : DenWriteMode.Replace);
                written.Add(path);
            }

            // A store that failed after replacing a file would leave it written but not
            // listed here; FileSystemDenStore's write is one move, so it can't.
            foreach (var path in written)
            {
                _sessionWritten[path] = files[path];
            }
        }
        catch (Exception e)
        {
            // One change or none (format.md F-RT-5): put back what was written, newest first,
            // whatever stopped the writes, cancellation included.
            var failed = files.Keys.Except(written).Order(StringComparer.Ordinal).First();
            var stuck = RollBack(written, files, originals, created);

            // A file put back is as the session found it; one that still holds the edit is the session's.
            foreach (var path in stuck.Where(p => Store.TryReadFile(p) is { } now && now.AsSpan().SequenceEqual(files[p])))
            {
                _sessionWritten[path] = files[path];
            }
            if (e is not (IOException or UnauthorizedAccessException))
            {
                if (stuck.Count > 0)
                {
                    throw new DenToolException(
                        ToolErrorCodes.WriteFailed,
                        $"Writing stopped ({e.GetType().Name}: {e.Message}), and some files written before it couldn't be put back, and hold the edit: {string.Join(", ", stuck)}. Check them, or restore them from history.");
                }

                throw;
            }

            var code = e is DenStoreException { Error: DenStoreError.AlreadyExists } ? ToolErrorCodes.AlreadyExists : ToolErrorCodes.WriteFailed;
            var said = e is DenStoreException ? e.Message : $"Couldn't write {failed}: {e.Message}";
            throw new DenToolException(
                code,
                said + (written.Count == 0 ? " Nothing was written."
                    : stuck.Count == 0 ? $" The files already written ({string.Join(", ", written)}) were put back as they were, so nothing changed."
                    : $" Some files written before it failed couldn't be put back, and hold the edit: {string.Join(", ", stuck)}. Check them, or restore them from history."));
        }
    }

    /// <summary>
    /// Puts back the files in <paramref name="written"/>, newest first: each
    /// to its original, or a created one deleted, but only if it still holds
    /// what was written (<paramref name="files"/>), so a save made meanwhile
    /// by another program is never overwritten. Returns the ones that couldn't be.
    /// </summary>
    private List<string> RollBack(List<string> written, IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<string, byte[]>? originals, string? created)
    {
        var stuck = new List<string>();
        foreach (var path in Enumerable.Reverse(written))
        {
            try
            {
                if (Store.TryReadFile(path) is not { } now || !now.AsSpan().SequenceEqual(files[path]))
                {
                    // Changed since it was written: someone else's edit now, so leave it.
                    stuck.Add(path);
                }
                else if (path == created)
                {
                    // Only a single-file write creates a file, and it's the one that failed if any did,
                    // so this isn't reached today; it's kept so a multi-file create rolls back too.
                    Store.DeleteFile(path);
                }
                else if (originals is not null && originals.TryGetValue(path, out var original))
                {
                    Store.WriteFile(path, original, DenWriteMode.Replace);
                }
                else
                {
                    stuck.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                stuck.Add(path);
            }
        }

        stuck.Reverse();
        return stuck;
    }

    private string? AddMissingId(CatalogEntity entity, Dictionary<string, byte[]> files)
    {
        if (entity.Id is not null)
        {
            return null;
        }

        var file = MarkdownFile.Parse(files[entity.Path], requireFrontmatter: true);
        var id = AddId(ref file);
        files[entity.Path] = file.ToBytes();
        return id;
    }

    /// <summary>Gives a file without an id a new one no file in the den uses (F-ID-6), or leaves it as it is if it can't be added in place.</summary>
    private string? AddId(ref MarkdownFile file, HashSet<string>? used = null)
    {
        used ??= WithIndex(index => index.Context.Entities.Where(e => e.Ulid != null).Select(e => e.Ulid!).ToList()
            .Concat(index.Context.Containers.Where(c => c.Ulid != null).Select(c => c.Ulid!)).ToHashSet(StringComparer.Ordinal));
        var id = NewUnusedId(used);
        try
        {
            file = file.WithId(id);
            return id;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or EditVerificationException)
        {
            // F-ID-6 says SHOULD: an id that can't be added in place is left for 'check --fix'.
            return null;
        }
    }

    /// <summary>Makes an edit, turning the editors' refusals into tool errors.</summary>
    private static MarkdownFile Apply(Func<MarkdownFile> edit)
    {
        try
        {
            return edit();
        }
        catch (DenToolException)
        {
            throw;
        }
        catch (ArgumentException e)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, e.Message.Split(" (Parameter", 2)[0]);
        }
        catch (InvalidOperationException e)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, e.Message);
        }
        catch (NotSupportedException e)
        {
            throw new DenToolException(ToolErrorCodes.UnsupportedEdit, $"{e.Message} Make this edit by hand, or ask the writer to.");
        }
        catch (EditVerificationException e)
        {
            throw new DenToolException(ToolErrorCodes.UnsupportedEdit, e.Message);
        }
    }

    // ------------------------------------------------------------ frontmatter

    /// <summary>One change in a <c>changes</c> list.</summary>
    private sealed record Change(string Op, IReadOnlyList<string>? Path, string? Field, YamlValue? Value, int? Index, int? Item, JsonElement? RawValue);

    /// <summary>The names types.md F-CHAP-1 reserves for a chapter item's own keys and later use.</summary>
    private static readonly HashSet<string> ReservedChapterNames =
        ["key", "number", "title", "id", "name", "status", "from", "until", "pin", "fields", "row", "placeholder", "scene"];

    private static List<Change> Changes(JsonElement changes, bool chapter)
    {
        if (changes.ValueKind == JsonValueKind.Object)
        {
            changes = JsonSerializer.SerializeToElement(new[] { changes });
        }

        if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() == 0)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "'changes' is a list of changes, such as [{\"op\": \"set\", " + (chapter ? "\"field\": \"tense\"" : "\"path\": \"fields.born\"") + ", \"value\": \"...\"}].");
        }

        var ops = chapter ? new[] { "set", "add", "remove", "clear" } : ["set", "add", "remove"];
        var result = new List<Change>();
        foreach (var item in changes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("op", out var opElement) || opElement.GetString() is not { } op || !ops.Contains(op))
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"Each change needs an 'op': one of {string.Join(", ", ops)}.");
            }

            var hasValue = item.TryGetProperty("value", out var value);
            if (op is "set" or "add" && !hasValue)
            {
                throw new DenToolException(ToolErrorCodes.BadArgument, $"A '{op}' change needs a 'value'.");
            }

            IReadOnlyList<string>? path = null;
            string? field = null;
            if (chapter)
            {
                field = item.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()!.Trim() : null;
                if (string.IsNullOrEmpty(field) || !Regex.IsMatch(field, "^[a-z][a-z0-9_]{0,63}$"))
                {
                    throw new DenToolException(ToolErrorCodes.BadArgument, "Each change needs a 'field': a chapter field name, such as 'pov' or 'tense'.");
                }

                if (ReservedChapterNames.Contains(field))
                {
                    throw new DenToolException(
                        ToolErrorCodes.BadArgument,
                        $"'{field}' is reserved for the chapter item's own keys (types.md F-CHAP-1), so it can't be a chapter field. The chapter's key, number, and title aren't edited here.");
                }
            }
            else
            {
                path = item.TryGetProperty("path", out var p) ? PathOf(p) : null;
                if (path is null)
                {
                    throw new DenToolException(ToolErrorCodes.BadArgument, "Each change needs a 'path', such as 'status', 'aliases', 'fields.born', or 'fields.rank.1.from'.");
                }
            }

            result.Add(new Change(op, path, field, hasValue ? ToYaml(value) : null, Int(item, "index"), Int(item, "item"), hasValue ? value : null));
        }

        return result;

        static int? Int(JsonElement item, string name) =>
            item.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 ? i
                : throw new DenToolException(ToolErrorCodes.BadArgument, $"'{name}' is a number from 0.") : null;
    }

    private static List<string>? PathOf(JsonElement p)
    {
        var segments = p.ValueKind switch
        {
            JsonValueKind.String => p.GetString()!.Split('.').Select(s => s.Trim()).ToList(),
            JsonValueKind.Array when p.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) => p.EnumerateArray().Select(e => e.GetString()!).ToList(),
            _ => null,
        };
        return segments is null || segments.Count == 0 || segments.Any(s => s.Length == 0) ? null : segments;
    }

    private static void CheckFrontmatterPath(IReadOnlyList<string> path, string op)
    {
        switch (path[0])
        {
            case "id":
                throw new DenToolException(ToolErrorCodes.BadArgument, "An id never changes (F-ID-4). A copy of an entity needs a new id, not an edited one.");
            case "name":
                throw new DenToolException(
                    ToolErrorCodes.UnsupportedEdit,
                    "Renaming an entity means rewriting the links that match the old name (F-RT-5): use rename_entity, which does that.");
            case "aliases" when op != "add" || path.Count != 1:
                throw new DenToolException(
                    ToolErrorCodes.UnsupportedEdit,
                    "Aliases can be added here, but not changed or removed: links that matched the old alias must follow (F-RT-5). Use change_alias, which rewrites them.");
            case "fields" when path.Count == 1 && op != "set":
                throw new DenToolException(ToolErrorCodes.BadArgument, "Name a field: 'fields.<name>'.");
            case "fields" when path.Count == 1:
                throw new DenToolException(ToolErrorCodes.BadArgument, "Set one field at a time: 'fields.<name>'.");
        }
    }

    private static MarkdownFile Remove(MarkdownFile file, IReadOnlyList<string> path, List<string> notes)
    {
        var parent = path.Count == 1 ? file.Frontmatter : file.GetFrontmatterValue(path.Take(path.Count - 1).ToList());
        var present = parent switch
        {
            YamlMapping m => m.ContainsKey(path[^1]),
            YamlSequence s => int.TryParse(path[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i < s.Items.Count,
            _ => false,
        };
        if (!present)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"There's nothing at '{string.Join('.', path)}' to remove.");
        }

        return NoteEmptied(notes, file.WithoutFrontmatterValue(path, EmptiedList.Null, out var comments), comments, string.Join('.', path.Take(path.Count - 1)));
    }

    /// <summary>
    /// Says, in <paramref name="notes"/>, how many comment lines went because
    /// a removal emptied the block mapping or list <paramref name="where"/>
    /// (format.md F-RT-6). One may have been a commented-out value, so the
    /// writer is told; a checkpoint, or the den's history, keeps the old text.
    /// </summary>
    private static MarkdownFile NoteEmptied(List<string> notes, MarkdownFile file, int comments, string where)
    {
        if (comments > 0)
        {
            notes.Add($"Removed {comments} comment line{(comments == 1 ? "" : "s")} inside {where}, which the change emptied.");
        }

        return file;
    }

    /// <summary>The pinned facts a frontmatter change touches: an entry with <c>pin: true</c> it sets, removes, or changes, or a new one it adds.</summary>
    private static IEnumerable<string> PinnedTouched(MarkdownFile file, IReadOnlyList<string> path, string op, YamlValue? value)
    {
        if (path[0] != "fields" || path.Count < 2)
        {
            yield break;
        }

        var name = $"fields.{path[1]}";
        var field = file.GetFrontmatterValue(["fields", path[1]]);
        var entries = field switch
        {
            YamlSequence list => list.Items.Select((item, i) => (Node: item, Index: (int?)i)).ToList(),
            YamlMapping single => [(single, null)],
            _ => [],
        };

        if (path.Count == 2)
        {
            if (op != "add" && entries.Any(e => IsPinned(e.Node)))
            {
                yield return $"{name} holds pinned canon, and the change {(op == "remove" ? "removes" : "replaces")} it";
            }
        }
        else if (field is YamlSequence list2 && int.TryParse(path[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i < list2.Items.Count)
        {
            if (IsPinned(list2.Items[i]) || (path.Count == 4 && path[3] == "pin"))
            {
                yield return $"{name}.{i} is pinned canon{(IsPinned(list2.Items[i]) ? "" : " (the change pins it)")}";
            }
        }
        else if (field is YamlMapping entry && (IsPinned(entry) || path[2] == "pin"))
        {
            yield return $"{name} is pinned canon{(IsPinned(entry) ? "" : " (the change pins it)")}";
        }

        if (value is YamlMappingValue m && m.Entries.Any(e => e.Key == "pin" && e.Value is YamlScalarValue v && v.ToString() == "true"))
        {
            yield return $"{name}: the {(op == "add" ? "new entry" : "value")} is pinned canon";
        }

        static bool IsPinned(YamlNode node) => node is YamlMapping m && m.ContainsKey("value") && m["pin"] is YamlScalar s && s.AsBoolean() == true;
    }

    /// <summary>
    /// What a new alias means for links: if another entity of the universe
    /// has that text as its slug, name, or an alias, a link by it reaches
    /// that entity first (an earlier tier, F-LINK-6), or, at the same tier,
    /// becomes ambiguous unless scope decides it (F-LINK-7).
    /// </summary>
    private IEnumerable<string> AliasEffects(CatalogEntity entity, string alias)
    {
        var normalized = LinkText.Normalize(alias);
        List<(string Name, string Path, string? Tier)> others;
        try
        {
            others = AliasMatches(entity, normalized);
        }
        catch (DenToolException)
        {
            // The write is saved already; a failure to read the index only loses this note.
            return [$"Whether another note also goes by '{alias}' couldn't be checked: the index couldn't be read. Call search or check to see."];
        }

        return others.Select(o => o.Tier == "alias"
            ? $"'{alias}' is also an alias of '{o.Name}' ({o.Path}), so a link [[{alias}]] is ambiguous between them unless one is in a nearer scope (F-LINK-7)."
            : $"'{alias}' is also the {o.Tier} of '{o.Name}' ({o.Path}), so a link [[{alias}]] still reaches '{o.Name}': a {o.Tier} matches before an alias (F-LINK-6).").ToList();
    }

    private List<(string Name, string Path, string? Tier)> AliasMatches(CatalogEntity entity, string normalized) =>
        WithIndex(index =>
        {
            var db = index.Context;
            var universe = db.Containers.Single(c => c.Kind == "universe" && c.Key == entity.Universe.Key).Id;
            var withAlias = db.Aliases.Where(a => a.AliasNormalized == normalized).Select(a => a.EntityId).ToList();
            return db.Entities.Where(e => e.UniverseId == universe && e.Path != entity.Path).ToList()
                .Select(e => (e.Name, e.Path, Tier: e.Slug is not null && LinkText.Normalize(e.Slug) == normalized ? "slug"
                    : e.NameNormalized == normalized ? "name" : withAlias.Contains(e.Id) ? "alias" : null))
                .Where(x => x.Tier is not null)
                .ToList();
        });

    /// <summary>What a frontmatter edit means beyond its bytes.</summary>
    private static IEnumerable<string> FieldEffects(CatalogEntity before, CatalogEntity after, IReadOnlyList<string> touched)
    {
        if (before.Status != after.Status)
        {
            yield return (before.Status.IsInCanon(), after.Status.IsInCanon()) switch
            {
                (true, false) => $"The entity's status is now '{after.Status.ToKey()}', so canon views leave it out, with all its sections and entries (F-STAT-3).",
                (false, true) => $"The entity's status is now '{after.Status.ToKey()}', so canon views show it again, from where it enters canon.",
                _ => $"The entity's status is now '{after.Status.ToKey()}'.",
            };
        }

        var fromBefore = before.File.Frontmatter?["from"] is YamlScalar { IsNull: false } fb ? fb.Text : null;
        var fromAfter = after.File.Frontmatter?["from"] is YamlScalar { IsNull: false } fa ? fa.Text : null;
        if (fromBefore != fromAfter)
        {
            yield return fromAfter is null
                ? $"It no longer has a 'from', so it enters canon at {after.Scope.StartDescription} (F-ENT-6)."
                : $"It now enters canon at {fromAfter}: views before that leave it out (F-ENT-6).";
        }

        if (before.Type != after.Type)
        {
            yield return after.Definition is { } type
                ? $"Its type is now '{type.Key}'. Its file is still in {before.Path[..before.Path.LastIndexOf('/')]}/; a '{type.Key}' belongs in entities/{type.Plural}/ (F-LAY-6)."
                : $"Its type is now '{after.Type}', which no enabled pack defines, so it's indexed untyped (F-ENT-2).";
        }

        foreach (var name in touched.Distinct())
        {
            var definition = after.Definition?.FindField(name);
            var value = (after.File.Frontmatter?["fields"] as YamlMapping)?[name];
            if (definition?.Default is { } fallback && value is null or YamlScalar { IsNull: true })
            {
                yield return $"fields.{name} is now unset, so views show the type's default for it: {JsonOf(fallback).GetRawText()} (types.md F-FT-15).";
            }
        }
    }

    // ------------------------------------------------------------- chapters

    private static int ItemToRemove(MarkdownFile file, string[] fieldPath, Change op, string owner = "The chapter's")
    {
        if (file.GetFrontmatterValue(fieldPath) is not YamlSequence list)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"{owner} '{op.Field}' isn't a list, so it has no item to remove. To unset it, use 'clear'.");
        }

        if (op.Item is { } item)
        {
            return item < list.Items.Count ? item : throw new DenToolException(ToolErrorCodes.BadArgument, $"{owner} '{op.Field}' has {list.Items.Count} item(s), so there's no item {item}.");
        }

        if (op.Value is YamlScalarValue value)
        {
            var i = list.Items.ToList().FindIndex(n => value.Matches(n) || (n is YamlScalar s && LinkText.Matches(s.Text, value.ToString())));
            return i >= 0 ? i : throw new DenToolException(ToolErrorCodes.BadArgument, $"{owner} '{op.Field}' has no item '{value}'.");
        }

        throw new DenToolException(ToolErrorCodes.BadArgument, "A 'remove' change names the item to remove: 'item' (its number from 0) or 'value'.");
    }

    private static MarkdownFile Clear(MarkdownFile file, string[] fieldPath, Chapter chapter, string field, List<string> notes)
    {
        if ((file.GetFrontmatterValue(fieldPath[..^1]) as YamlMapping)?.ContainsKey(field) != true)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, $"Chapter '{chapter.Key}' doesn't set '{field}' itself, so there's nothing to clear: it already inherits.");
        }

        return NoteEmptied(notes, file.WithoutFrontmatterValue(fieldPath, EmptiedList.Null, out var comments), comments, $"the fields of chapter '{chapter.Key}'");
    }

    private static List<string> ChapterEffects(Chapter before, Chapter after, IReadOnlyList<string> fields)
    {
        var effects = new List<string>();
        var label = $"Chapter {after.Book.Key}:{after.Key}{(after.Title is { } t ? $" ({t})" : "")}";
        foreach (var name in fields)
        {
            var own = after.OwnFields.GetValueOrDefault(name);
            var effective = after.Fields.GetValueOrDefault(name);
            var inherited = Inherited(after, name);
            var inheritedText = inherited is null ? null : $"{Describe(inherited)} from {Where(inherited.DefaultFrom!)}";
            var hadOwn = before.OwnFields.ContainsKey(name);
            if (own is { IsNone: true })
            {
                effects.Add($"{label} now has no {name} ({name}: [], \"none here\")." +
                    (inheritedText is null ? "" : $" Without its own value it would inherit {inheritedText}; to go back to that default, clear the field."));
            }
            else if (own is not null)
            {
                effects.Add($"{label}'s {name} is now {Describe(own)}, its own value" +
                    (inheritedText is null ? "." : $", in place of {inheritedText}."));
            }
            else if (effective is not null)
            {
                effects.Add($"{label} now inherits {name}: {Describe(effective)} from {Where(effective.DefaultFrom!)}.");
            }
            else
            {
                effects.Add($"{label} now has no {name}: neither the chapter nor any chapter_defaults above it sets one." +
                    (hadOwn ? "" : ""));
            }
        }

        return effects;
    }

    /// <summary>What a chapter would inherit for a field without its own value: the nearest <c>chapter_defaults</c> that sets it.</summary>
    private static ChapterFieldValue? Inherited(Chapter chapter, string name) =>
        chapter.Book.ScopeChain.Select(c => c.ChapterDefaults.GetValueOrDefault(name)).FirstOrDefault(v => v is not null);

    private static string Where(Container c) => $"{c.Kind.ToString().ToLowerInvariant()} '{c.Key}' chapter_defaults";

    private static ChapterView ChapterOf(Chapter chapter)
    {
        var names = chapter.Fields.Keys.Concat(chapter.OwnFields.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var fields = names.Select(name =>
        {
            var effective = chapter.Fields.GetValueOrDefault(name);
            var own = chapter.OwnFields.GetValueOrDefault(name);
            var inherited = Inherited(chapter, name);
            return new ChapterFieldView(
                name,
                effective is null ? null : JsonOf(effective.Value),
                effective is null ? null : effective.DefaultFrom is { } c ? c.Kind.ToString().ToLowerInvariant() : "chapter",
                effective?.DefaultFrom?.Key,
                effective?.IsNone == true,
                own is null ? null : JsonOf(own.Value),
                inherited is null ? null : JsonOf(inherited.Value),
                inherited?.DefaultFrom is { } d ? d.Kind.ToString().ToLowerInvariant() : null);
        }).ToList();
        return new ChapterView(chapter.Book.Key, chapter.Key, chapter.Number, chapter.Title, chapter.Line, fields);
    }

    // ------------------------------------------------------------- values

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "Give the entity's name in 'name': one line of text.");
        }
    }

    private static List<KeyValuePair<string, YamlValue>> FieldValues(JsonElement fields)
    {
        if (fields.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (fields.ValueKind != JsonValueKind.Object)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, "'fields' is an object from field name to value, such as {\"born\": \"1873\"}.");
        }

        return fields.EnumerateObject().Select(p => new KeyValuePair<string, YamlValue>(p.Name, ToYaml(p.Value))).ToList();
    }

    /// <summary>A JSON value as YAML to write: text as strings, whole numbers, true and false, null, lists, and mappings.</summary>
    private static YamlValue ToYaml(JsonElement value)
    {
        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return YamlScalarValue.String(value.GetString()!);
                case JsonValueKind.Number when value.TryGetInt64(out var n):
                    return YamlScalarValue.Integer(n);
                case JsonValueKind.Number:
                    throw new DenToolException(ToolErrorCodes.BadArgument, $"{value.GetRawText()} isn't a whole number; write it as text, \"{value.GetRawText()}\".");
                case JsonValueKind.True or JsonValueKind.False:
                    return YamlScalarValue.Boolean(value.GetBoolean());
                case JsonValueKind.Null:
                    return YamlScalarValue.Null;
                case JsonValueKind.Array:
                    var list = new YamlSequenceValue();
                    foreach (var item in value.EnumerateArray())
                    {
                        list.Add(ToYaml(item));
                    }

                    return list;
                default:
                    var map = new YamlMappingValue();
                    foreach (var p in value.EnumerateObject())
                    {
                        map.Add(p.Name, ToYaml(p.Value));
                    }

                    return map;
            }
        }
        catch (ArgumentException e)
        {
            throw new DenToolException(ToolErrorCodes.BadArgument, e.Message.Split(" (Parameter", 2)[0]);
        }
    }

    /// <summary>A YAML value as JSON: scalars as their text, nulls as null.</summary>
    private static JsonElement JsonOf(YamlNode? node)
    {
        return JsonSerializer.SerializeToElement(Plain(node));

        static object? Plain(YamlNode? node) => node switch
        {
            null or YamlScalar { IsNull: true } => null,
            YamlScalar s => s.Text,
            YamlSequence l => l.Items.Select(Plain).ToList(),
            YamlMapping m => m.Entries.Where(e => e.KeyText is not null).GroupBy(e => e.KeyText!).ToDictionary(g => g.Key, g => Plain(g.First().Value)),
            _ => null,
        };
    }

    private static EntitySummary Summary(CatalogEntity e) => new(
        e.Key, e.Id, e.Name, e.Type, e.Status.ToKey(), e.Universe.Key, e.Scope.Kind.ToString().ToLowerInvariant(), e.Scope.Key, e.Path,
        e.File.Frontmatter?["from"] is YamlScalar { IsNull: false } f ? f.Text : null);

    private static List<DiagnosticView> Warnings(DenWriteCheckResult check) =>
        check.Warnings.Select(d => new DiagnosticView(d.Path, d.Diagnostic.Line, "warning", d.Diagnostic.RuleId, d.Diagnostic.Message)).ToList();

    /// <summary>The SHA-256 of a file's bytes in lowercase hex, as the index records it.</summary>
    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    [GeneratedRegex(@"^[^<>\r\n]*[^<>\s][^<>\r\n]* <[^<>\s]+>$")]
    private static partial Regex CoAuthorPattern();
}
