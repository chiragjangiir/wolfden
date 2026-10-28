using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wolfden.Mcp;

/// <summary>
/// The MCP tool surface (design.md §8): each method is one tool, with a
/// description written for an AI client. The work is done by
/// <see cref="DenTools"/>; these methods turn its results into JSON, and its
/// errors into error results with a code and a message.
/// </summary>
/// <remarks>
/// Argument names are snake_case, as MCP tools' usually are, so the C#
/// parameter names are too.
/// </remarks>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "MCP argument names are snake_case.")]
public sealed class WolfdenTools
{
    private const string PositionHelp =
        "A story position is 'book:chapter' using the keys from the den (such as 'dracula:5'), or a bare book key for the start of that book. " +
        "Positions follow reading order across the whole universe.";

    private const string UniverseHelp =
        "The universe (its key). A den with one universe uses it; in a den with several, name one, unless a story position " +
        "names a book only one universe has.";

    private readonly DenTools _tools;

    /// <summary>The tools over <paramref name="tools"/>' den.</summary>
    public WolfdenTools(DenTools tools)
    {
        _tools = tools;
    }

    /// <summary>The <c>search</c> tool.</summary>
    [McpServerTool(Name = "search", Title = "Search the den", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Full-text search over the den's notes: entity names, aliases, leads, section text, and field values. Words match whole, " +
        "in any order, ignoring case and accents; end a word with * to match it as a prefix. Put words in double quotes to match " +
        "them as a phrase, side by side and in order (\"silver key\", or \"silver ke\"* for a prefix); a hyphenated word such as " +
        "mara-voss matches its parts side by side too. Phrases and words mix: \"silver key\" harbour. A field value's words are the value " +
        "as written and, for a link, the linked entity's name and aliases. Give field to search only that field's values (field " +
        "'home' with query 'saltmarsh' finds home: Saltmarsh Docks); each hit in a field names it in 'field', and its snippet " +
        "shows the value, then '→' and a linked entity's name if that's what matched. Matches are in [square brackets]. Results " +
        "include every status (ideas, undecided questions, retconned material) unless canon_only is true, and each hit says its " +
        "status and whether it's canon text. Search has no story position, so it can show material the reader meets later: use " +
        "canon_as_of or get_entity with as_of for spoiler-safe reading.")]
    public CallToolResult Search(
        [Description("The words to search for.")] string query,
        [Description(UniverseHelp + " Here '*' searches every universe.")] string? universe = null,
        [Description("Only entities of this type, such as 'character' or 'location'.")] string? type = null,
        [Description("If true, only canon text: draft or canon material, not ideas, retconned material, or bookkeeping sections.")] bool canon_only = false,
        [Description("The most hits to return, 1 to 200.")] int limit = 20,
        [Description("Search only this field's values (the frontmatter key, such as 'home'), not names, leads, or sections.")] string? field = null) =>
        Respond(() => _tools.Search(query, universe, type, canon_only, limit, field));

    /// <summary>The <c>get_entity</c> tool.</summary>
    [McpServerTool(Name = "get_entity", Title = "Read one entity", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Reads one entity (a character, location, event, item, faction, thread, note, or other type) with its fields and sections. " +
        "Name it as a link would: by name, alias, file name, or id; or by its key or path. Pinned facts (the hard canon to check " +
        "before writing) come first, under 'pinned', and are also listed in place. Without as_of you get the author's full view: " +
        "every field entry and section, whatever its status (idea, undecided, candidate, draft, canon, retconned), with the " +
        "positions where each one starts and ends. With as_of you get only what's canon at that point in the story, as a reader " +
        "would know it then: the name is matched only among the entities the reader has met by then, " +
        "and if the reader hasn't met the entity yet, in_view is false and nothing about it is returned. " +
        PositionHelp)]
    public CallToolResult GetEntity(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description(UniverseHelp + " With as_of, the position's book settles it.")] string? universe = null,
        [Description("A story position, such as 'dracula:5', to read the entity as canon stands there. Leave it out for the full view.")] string? as_of = null) =>
        Respond(() => _tools.GetEntity(entity, universe, as_of));

    /// <summary>The <c>list_entities</c> tool.</summary>
    [McpServerTool(Name = "list_entities", Title = "List entities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Lists entities as one-line summaries (key, name, type, status, where it's filed, and where it enters canon), of every " +
        "status unless status is given. A den holds universes; a universe holds series of books and standalone books. Each entity " +
        "is filed at one scope: the universe, a series, or a book. A series or book filter keeps the entities of that series or " +
        "book, of the scopes inside it, and of the scopes that hold it (as canon_as_of's filter does). Use get_entity to read one.")]
    public CallToolResult ListEntities(
        [Description(UniverseHelp + " Here '*' lists every universe (not with series or book).")] string? universe = null,
        [Description("Only this type, such as 'character'.")] string? type = null,
        [Description("Only this status: idea, undecided, candidate, draft, canon, or retconned.")] string? status = null,
        [Description("Only this series (its key) and what it holds.")] string? series = null,
        [Description("Only this book (its key) and the scopes that hold it.")] string? book = null,
        [Description("The most entities to return, 1 to 1000.")] int limit = 200) =>
        Respond(() => _tools.ListEntities(universe, type, status, series, book, limit));

    /// <summary>The <c>canon_as_of</c> tool.</summary>
    [McpServerTool(Name = "canon_as_of", Title = "Canon as of a story position", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "What's canon at a point in the story: every entity the reader has met by then, at any scope of the universe, with the " +
        "field values and sections in effect there. Ideas, undecided questions, retconned material, and bookkeeping sections are " +
        "left out, and draft material is marked draft. Nothing the reader meets later is included, so it's safe for drafting a " +
        "chapter without spoilers. Pinned canon (the hard facts) comes first, under 'pinned', and is also listed in place. " +
        "Without position, the view is the end of the story: all canon so far. " + PositionHelp)]
    public CallToolResult CanonAsOf(
        [Description("A story position, such as 'dracula:5'. Leave it out for the end of the story.")] string? position = null,
        [Description(UniverseHelp + " With position, the position's book settles it.")] string? universe = null,
        [Description("Only entities filed in this series, its books, or the universe.")] string? series = null,
        [Description("Only entities filed in this book, its series, or the universe.")] string? book = null,
        [Description("Only entities of this type, such as 'character'.")] string? type = null,
        [Description("If true, list the entities without their fields and sections (pinned canon is still given in full).")] bool brief = false) =>
        Respond(() => _tools.CanonAsOf(universe, position, series, book, type, brief));

    /// <summary>The <c>check</c> tool.</summary>
    [McpServerTool(Name = "check", Title = "Check the den", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Runs Wolfden's continuity checks on the den's files, as 'wolfden check' does, and returns the findings: broken or " +
        "ambiguous links, positions that don't resolve, missing required fields or sections, overlapping values, duplicate ids, " +
        "and (when the den turns them on) spoiler checks. Each finding has a path, a line, a severity (error or warning), a rule " +
        "id from Wolfden's format spec, and a message. Errors keep a file out of the index; warnings don't. It never changes a file. " +
        "Unlike the other tools it checks the whole den by default, every universe included, since it validates files rather than " +
        "a story view; give a universe to check only that universe's files and the den-level ones (den.yaml and packs). Ids are " +
        "still compared with the other universes' notes, so a duplicate id across universes is found; if that comparison couldn't " +
        "run, 'note' says why.")]
    public CallToolResult Check(
        [Description("Only 'error' or only 'warning'.")] string? severity = null,
        [Description("Only files under this path, relative to the den root, such as 'universes/dracula/books'.")] string? path = null,
        [Description("The most findings to return, 1 to 5000. The counts cover everything checked.")] int limit = 200,
        [Description("Check only this universe (its key) and the den-level files. Leave it out, or give '*', for the whole den.")] string? universe = null) =>
        Respond(() => _tools.Check(severity, path, limit, universe));

    /// <summary>The <c>get_history</c> tool.</summary>
    [McpServerTool(Name = "get_history", Title = "An entity's history", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "The version history of one entity's file: the checkpoints (git commits) that changed it, newest first, each with its id, " +
        "time, author, message, and the files it changed. Name the entity as for get_entity. Results come a page at a time: when " +
        "'truncated' is true, ask again with before set to 'next_before' for older checkpoints. With diff true, the result also " +
        "has what changed in the file since the latest checkpoint (edits not yet checkpointed); with diff_from set to a checkpoint's " +
        "id, what changed since that checkpoint. The history follows the file's current path, so it starts where the file was last " +
        "moved or renamed. If the den has no history (it isn't in a git repository, or git isn't installed), the result isn't an " +
        "error: 'available' is false and 'reason' says why. History isn't a story view, so it can show material the reader meets later.")]
    public CallToolResult GetHistory(
        [Description(
            "The entity: its name, an alias, its file name, its id, its key, or its path. A note with errors, which the den leaves out " +
            "until they're fixed, is named by its path.")] string entity,
        [Description(UniverseHelp)] string? universe = null,
        [Description("The most checkpoints to return, 1 to 100.")] int limit = 20,
        [Description("Only checkpoints older than this one (an id from an earlier result's 'next_before'), for the next page.")] string? before = null,
        [Description("If true, also show what changed in the file since the latest checkpoint.")] bool diff = false,
        [Description("Show what changed in the file since this checkpoint (an id from the history), up to the file as it is now.")] string? diff_from = null) =>
        Respond(() => _tools.GetHistory(entity, universe, limit, before, diff, diff_from));

    private const string WriteHelp =
        " The edit goes to the den's files only (nothing is committed until checkpoint), and is checked as the den would read it: " +
        "an edit that would leave an error is refused (invalid_edit), and nothing is written. Nothing in a final (frozen) book can be " +
        "changed (frozen). The result gives the file's new hash, what the edit means where that depends on more than the text written " +
        "('effects'), any pinned canon it touched ('pinned_canon': check those with the writer), and warnings about the file.";

    private const string HashHelp =
        "The hash of the file your edit is based on: 'hash' from get_entity, or from your last write's result. If the file has changed " +
        "since (say, the writer edited it), the write is refused with stale_file: read it again.";

    /// <summary>The <c>create_entity</c> tool.</summary>
    [McpServerTool(Name = "create_entity", Title = "Create an entity", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Creates a new entity (a character, location, event, item, faction, thread, note, idea, or other type an enabled pack defines) " +
        "from its type's template, with a new id. It's filed in the universe, or in the series or book given, under " +
        "entities/<type folder>/<slug>.md; the slug is made from the name unless given, and gets -2, -3 if that file is taken. " +
        "Values you give (aliases, status, from, fields) replace the template's; the status defaults to the template's, or draft. " +
        "The template's sections come with it, empty: fill them with append_section. If the new name or file name would change which " +
        "entity an existing link reaches (another one, or none because it becomes ambiguous; a nearer scope wins, F-LINK-7), nothing is " +
        "created unless allow_relinks is true; a broken link the new entity now mends is listed in effects. A 'from' must be a position the " +
        "universe has (bad_position otherwise)." + WriteHelp)]
    public CallToolResult CreateEntity(
        [Description("The type key, such as 'character'.")] string type,
        [Description("The entity's name.")] string name,
        [Description(UniverseHelp)] string? universe = null,
        [Description("File it in this series (its key).")] string? series = null,
        [Description("File it in this book (its key).")] string? book = null,
        [Description("The file name without .md: lowercase letters and digits with single hyphens. Leave it out to make one from the name.")] string? slug = null,
        [Description("Other names links can use.")] string[]? aliases = null,
        [Description("idea, undecided, candidate, draft, canon, or retconned.")] string? status = null,
        [Description("Where the reader first meets it, as a position such as 'dracula:5'. Leave it out for the start of where it's filed.")] string? from = null,
        [Description("Field values, as an object from field name to value, such as {\"born\": \"1873\", \"occupation\": [\"solicitor\"]}. A value that changes over time is a list of entries: [{\"value\": \"x\", \"from\": \"dracula:3\"}].")] JsonElement? fields = null,
        [Description("Text for the lead, before the first section.")] string? text = null,
        [Description(RelinksHelp)] bool allow_relinks = false) =>
        Respond(() => _tools.CreateEntity(type, name, universe, series, book, slug, aliases, status, from, fields, text, allow_relinks));

    /// <summary>The <c>update_fields</c> tool.</summary>
    [McpServerTool(Name = "update_fields", Title = "Change an entity's fields", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Changes an entity's frontmatter, keeping everything else as written. 'changes' is a list, made in order and saved together " +
        "or not at all. Each change is {\"op\": \"set\", \"path\": ..., \"value\": ...}, {\"op\": \"add\", \"path\": ..., \"value\": ..., \"index\": n} " +
        "(add an item to a list: an alias, or an entry of a field that changes over time; at the end without index), or " +
        "{\"op\": \"remove\", \"path\": ...} (a key or list item). A path is dotted: 'status', 'from', 'aliases', 'fields.born', " +
        "'fields.rank.1.from' (item numbers from 0). An entry is {\"value\": x, \"from\": \"book:chapter\", \"until\": ..., \"status\": ..., \"pin\": true}. " +
        "The id never changes. Aliases can be added here; to rename the entity, or change or remove an alias, use rename_entity or " +
        "change_alias, which rewrite the links that used the old text." + WriteHelp)]
    public CallToolResult UpdateFields(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description(HashHelp)] string expected_hash,
        [Description("The changes, in order: a list of {op, path, value, index}.")] JsonElement changes,
        [Description(UniverseHelp)] string? universe = null) =>
        Respond(() => _tools.UpdateFields(entity, expected_hash, changes, universe));

    /// <summary>The <c>append_section</c> tool.</summary>
    [McpServerTool(Name = "append_section", Title = "Add to a section", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Adds text to the end of an entity's section's own text, after what's there and before any subsections it has (template prompts " +
        "in HTML comments stay), or adds the section " +
        "if the entity has none of that name. With new_section true it adds another section of that name even if there is one, such as a " +
        "new 'Appearance' from a later chapter: give it a 'from' where it takes over. A new section can carry 'from', 'until', 'status', " +
        "and 'pin', written as its section comment; a standard section of the type goes in the type's order, any other at the end, " +
        "and with 'under' it's a subsection at the end of that section. To keep reveals in place, put a later fact in a subsection with " +
        "its own 'from'." + WriteHelp)]
    public CallToolResult AppendSection(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description("The section's name (heading text).")] string section,
        [Description("The markdown text to add.")] string text,
        [Description(HashHelp)] string expected_hash,
        [Description(UniverseHelp)] string? universe = null,
        [Description("Add it as a subsection of this section.")] string? under = null,
        [Description("When several sections have the name, the line of the heading you mean (from get_entity).")] int? section_line = null,
        [Description("If true, add a new section even if one has this name.")] bool new_section = false,
        [Description("For a new section: where it enters canon, a position such as 'dracula:7'.")] string? from = null,
        [Description("For a new section: where it stops holding (exclusive).")] string? until = null,
        [Description("For a new section: idea, undecided, candidate, draft, canon, or retconned.")] string? status = null,
        [Description("For a new section: true to pin it as hard canon.")] bool? pin = null) =>
        Respond(() => _tools.AppendSection(entity, section, text, expected_hash, universe, under, section_line, new_section, from, until, status, pin));

    /// <summary>The <c>add_idea</c> tool.</summary>
    [McpServerTool(Name = "add_idea", Title = "Record an idea", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Records undecided material with status idea, so canon views leave it out until the writer settles it. With 'entity', it adds a " +
        "new section to that entity (named by 'section', 'Idea' by default) marked status=idea, and needs expected_hash. Without it, it " +
        "creates a new entity with status idea, named 'name', of 'type' (by default 'idea', for an idea that isn't yet a thing of any " +
        "one type; a character who might exist is a 'character'), with the text in its lead. If the new entity's name or file name " +
        "would change which entity an existing link reaches (another one, or none because it becomes ambiguous; a nearer scope wins, " +
        "F-LINK-7), nothing is created unless allow_relinks is true; a broken link it now mends is listed in effects." + WriteHelp)]
    public CallToolResult AddIdea(
        [Description("The idea, in markdown.")] string text,
        [Description("Add the idea to this existing entity.")] string? entity = null,
        [Description("With entity: the new section's name.")] string? section = null,
        [Description("With entity: " + HashHelp)] string? expected_hash = null,
        [Description("Without entity: the new idea's name.")] string? name = null,
        [Description("Without entity: its type, 'idea' by default.")] string? type = null,
        [Description(UniverseHelp)] string? universe = null,
        [Description("Without entity: file it in this series.")] string? series = null,
        [Description("Without entity: file it in this book.")] string? book = null,
        [Description("Without entity: field values, such as {\"about\": [\"[[count-dracula]]\"]}.")] JsonElement? fields = null,
        [Description("Without entity: " + RelinksHelp)] bool allow_relinks = false) =>
        Respond(() => _tools.AddIdea(text, entity, section, expected_hash, name, type, universe, series, book, fields, allow_relinks));

    /// <summary>The <c>rename_section</c> tool.</summary>
    [McpServerTool(Name = "rename_section", Title = "Rename a section", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Renames a section of an entity, and in the same change rewrites every link to it by heading ([[entity#Old name]]) in any " +
        "file of the den, so they name the new heading. If a file that links to it is in a final (frozen) book, nothing is renamed. " +
        "The result lists every file written, with its new hash." + WriteHelp)]
    public CallToolResult RenameSection(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description("The section's current name.")] string section,
        [Description("The new name: one line, and without [, ], |, or # if links name the section.")] string new_name,
        [Description(HashHelp)] string expected_hash,
        [Description(UniverseHelp)] string? universe = null,
        [Description("When several sections have the name, the line of the heading you mean.")] int? section_line = null) =>
        Respond(() => _tools.RenameSection(entity, section, new_name, expected_hash, universe, section_line));

    /// <summary>The <c>rename_entity</c> tool.</summary>
    [McpServerTool(Name = "rename_entity", Title = "Rename an entity", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Changes an entity's name, and in the same change rewrites every link that reached it by the old name ([[Old name]], with any " +
        "#heading and label kept) in any file of the den, so they use the new name. A title heading that reads the old name follows; " +
        "the file isn't renamed, so links by file name or id are untouched. With keep_old_name true, the old name becomes an alias " +
        "instead and no link changes, which suits a name that changes in the story. If a link to rewrite is in a final (frozen) book, " +
        "nothing changes: keep the old name instead. A new name that would send a rewritten link elsewhere is refused. The result lists " +
        "every file written, with its new hash, and any other link that now resolves differently. If a link that reaches another " +
        "entity would change target (a new name that is its alias takes its links), nothing changes unless allow_relinks is true." + WriteHelp)]
    public CallToolResult RenameEntity(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description("The new name: one line, and without [, ], |, or # if links use the old name.")] string new_name,
        [Description(HashHelp)] string expected_hash,
        [Description("If true, keep the old name as an alias and leave links as they are.")] bool keep_old_name = false,
        [Description(UniverseHelp)] string? universe = null,
        [Description(RelinksHelp)] bool allow_relinks = false) =>
        Respond(() => _tools.RenameEntity(entity, new_name, expected_hash, keep_old_name, universe, allow_relinks));

    /// <summary>The <c>change_alias</c> tool.</summary>
    [McpServerTool(Name = "change_alias", Title = "Change or remove an alias", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Changes one of an entity's aliases to new_alias, or removes it when new_alias is left out, and in the same change rewrites " +
        "every link that reached the entity by the old alias, in any file of the den: to the new alias, or to the entity's name for a " +
        "removed one. With keep_old_alias true, the new alias is added beside the old one instead and no link changes. If a link to " +
        "rewrite is in a final (frozen) book, nothing changes. If a link that reaches another entity would change target, nothing " +
        "changes unless allow_relinks is true. To add an alias, update_fields is enough." + WriteHelp)]
    public CallToolResult ChangeAlias(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description("The alias to change or remove, as it's written.")] string alias,
        [Description(HashHelp)] string expected_hash,
        [Description("The new alias; leave it out to remove the alias.")] string? new_alias = null,
        [Description("If true, add new_alias beside the old one and leave links as they are.")] bool keep_old_alias = false,
        [Description(UniverseHelp)] string? universe = null,
        [Description(RelinksHelp)] bool allow_relinks = false) =>
        Respond(() => _tools.ChangeAlias(entity, alias, expected_hash, new_alias, keep_old_alias, universe, allow_relinks));

    /// <summary>The <c>move_entity</c> tool.</summary>
    [McpServerTool(Name = "move_entity", Title = "Move an entity to another scope", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Moves an entity's file to another scope of its universe: into a series (to_series), into a book (to_book), or, with neither, " +
        "into the universe itself. The file keeps its name and its folders under entities/. Canon views stay as they were: an entity " +
        "with no 'from' gets the point where it entered canon written as 'from', and a link to it that would stop reaching it (scope " +
        "breaks ties between entities with the same name) is rewritten to its id, showing the same text. The moved entity's own links " +
        "that would reach a different entity, or become ambiguous, from the new scope are rewritten to the id of what they reach now, " +
        "keeping their text, #section, and label, and listed in effects. If the reader meets the " +
        "entity outside the new scope (a series character first met in Book 1, moved into Book 2), the move is refused " +
        "(entry_outside_scope): ask the writer, then call again with keep_entry true (keep where it enters; check then warns about a " +
        "book entity's 'from' outside its book) or enter_at_new_scope true (it enters at the start of the new scope instead; for one " +
        "the reader meets only after the new scope, that's earlier than readers meet it now). Nothing " +
        "moves into or out of a final (frozen) book. If a link in another file that reaches another entity would change target, nothing " +
        "changes unless allow_relinks is true; allow_relinks also leaves the moved entity's own links as written, to follow the new " +
        "scope. The result's path and hash are the file's new ones, and 'files' marks the moved file with its " +
        "old path. A checkpoint with 'paths' needs both the old and the new path." + WriteHelp)]
    public CallToolResult MoveEntity(
        [Description("The entity: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description(HashHelp)] string expected_hash,
        [Description("Move it into this series (its key).")] string? to_series = null,
        [Description("Move it into this book (its key).")] string? to_book = null,
        [Description("If it enters canon outside the new scope: true to keep where it enters, as its 'from'.")] bool keep_entry = false,
        [Description("If it enters canon outside the new scope: true to have it enter at the start of the new scope instead.")] bool enter_at_new_scope = false,
        [Description(UniverseHelp)] string? universe = null,
        [Description(RelinksHelp)] bool allow_relinks = false) =>
        Respond(() => _tools.MoveEntity(entity, expected_hash, to_series, to_book, keep_entry, enter_at_new_scope, universe, allow_relinks));

    /// <summary>The <c>copy_entity</c> tool.</summary>
    [McpServerTool(Name = "copy_entity", Title = "Copy an entity", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Copies an entity to start a new one from it, such as another character like this one. The copy gets a new id that no file in " +
        "the den uses, new_name as its name, and a file name made from it (-2, -3 if taken), and no aliases (they stay the original's); a " +
        "title heading that reads the old name takes the new one. Its type, fields, sections, status, and from and until are kept, and " +
        "links in it are left as they are. It's filed beside the original, or in to_universe, to_series, or to_book, in the original's " +
        "folder path under entities/; never into a final (frozen) book. Copied into another universe, links that break there are listed in effects, not " +
        "refused. A copy without 'from' enters canon at the start of where it's filed: effects say so. If the copy's name or file name " +
        "would change which entity an existing link reaches (another one, or none because it becomes ambiguous), nothing is copied unless " +
        "allow_relinks is true; a broken link the copy now mends is listed in effects. Use create_entity to start from the type's template instead." + WriteHelp)]
    public CallToolResult CopyEntity(
        [Description("The entity to copy: its name, an alias, its file name, its id, its key, or its path.")] string entity,
        [Description("The copy's name.")] string new_name,
        [Description(UniverseHelp + " This is the original's universe.")] string? universe = null,
        [Description("Copy into this universe (its key), at universe scope unless to_series or to_book is given. By default, the original's.")] string? to_universe = null,
        [Description("Copy into this series (its key).")] string? to_series = null,
        [Description("Copy into this book (its key).")] string? to_book = null,
        [Description("The copy's file name without .md: lowercase letters and digits with single hyphens. Leave it out to make one from new_name.")] string? slug = null,
        [Description(RelinksHelp)] bool allow_relinks = false,
        [Description("Optional: the original's hash from get_entity, to refuse the copy (stale_file) if it has changed since you read it.")] string? expected_hash = null) =>
        Respond(() => _tools.CopyEntity(entity, new_name, universe, to_universe, to_series, to_book, slug, allow_relinks, expected_hash));

    private const string RelinksHelp =
        "If true, make the change even though links that reach another entity would change target; they're listed in the result's effects. " +
        "Check them with the writer first.";

    /// <summary>The <c>get_chapter</c> tool.</summary>
    [McpServerTool(Name = "get_chapter", Title = "Read a book's chapters", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "A book's chapters, or one chapter, with each chapter field (such as pov, narration, tense, when): its effective value, " +
        "whether that's the chapter's own or inherited from a chapter_defaults (of the book, series, or universe), and what it would " +
        "inherit without its own value. A list field set to [] means 'none here' and stops inheritance. Also gives the hash of the " +
        "book's book.md, for update_chapter_fields.")]
    public CallToolResult GetChapter(
        [Description("The book's key.")] string book,
        [Description("The chapter's key; leave it out for every chapter.")] string? chapter = null,
        [Description(UniverseHelp)] string? universe = null) =>
        Respond(() => _tools.GetChapter(book, chapter, universe));

    /// <summary>The <c>update_chapter_fields</c> tool.</summary>
    [McpServerTool(Name = "update_chapter_fields", Title = "Change a chapter's fields", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Changes a chapter's own fields in book.md. 'changes' is a list of {\"op\": \"set\", \"field\": \"tense\", \"value\": \"present\"}, " +
        "{\"op\": \"add\", \"field\": \"pov\", \"value\": \"[[mina-harker]]\"}, {\"op\": \"remove\", \"field\": \"pov\", \"item\": 0} (or \"value\": the " +
        "item's text, compared ignoring case and spacing; a link matches only if it's written the same way, so [[Mina]] doesn't match " +
        "[[mina-harker]]: use 'item' from get_chapter if unsure), and {\"op\": \"clear\", \"field\": \"pov\"}. Removing a list's last item leaves [], 'none here', which stops the chapter " +
        "inheriting the field; 'clear' removes the chapter's own value so it inherits from chapter_defaults again. The result says " +
        "what each field's effective value is now, and what the chapter would inherit, so check with the writer which they meant." + WriteHelp)]
    public CallToolResult UpdateChapterFields(
        [Description("The book's key.")] string book,
        [Description("The chapter's key.")] string chapter,
        [Description("The hash of book.md your edit is based on: 'hash' from get_chapter, or from your last write's result.")] string expected_hash,
        [Description("The changes, in order: a list of {op, field, value, index, item}.")] JsonElement changes,
        [Description(UniverseHelp)] string? universe = null) =>
        Respond(() => _tools.UpdateChapterFields(book, chapter, expected_hash, changes, universe));

    /// <summary>The <c>get_chapter_defaults</c> tool.</summary>
    [McpServerTool(Name = "get_chapter_defaults", Title = "Read chapter defaults", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "The chapter_defaults of a universe, a series (with 'series'), or a book (with 'book'): the chapter field values (such as pov, " +
        "narration, tense) its books' chapters inherit, field by field, unless a nearer container or the chapter sets its own. Each " +
        "field gives this container's own value and what its books would inherit from above without it. A list field set to [] means " +
        "'none here' and stops inheritance. Also lists the books it reaches (and which are final), and gives the hash of its file " +
        "(universe.md, series.md, or book.md), for update_chapter_defaults.")]
    public CallToolResult GetChapterDefaults(
        [Description(UniverseHelp)] string? universe = null,
        [Description("A series' key, for that series' defaults.")] string? series = null,
        [Description("A book's key, for that book's defaults.")] string? book = null) =>
        Respond(() => _tools.GetChapterDefaults(universe, series, book));

    /// <summary>The <c>update_chapter_defaults</c> tool.</summary>
    [McpServerTool(Name = "update_chapter_defaults", Title = "Change chapter defaults", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Changes the chapter_defaults of a universe, a series (with 'series'), or a book (with 'book'), such as 'make Mina the default POV " +
        "for this book'. 'changes' is a list of {\"op\": \"set\", \"field\": \"tense\", \"value\": \"past\"}, {\"op\": \"add\", \"field\": \"pov\", " +
        "\"value\": \"[[mina-harker]]\"}, {\"op\": \"remove\", \"field\": \"pov\", \"item\": 0} (or \"value\": the item's text), and " +
        "{\"op\": \"clear\", \"field\": \"pov\"}. Removing a list's last item leaves [], 'none here', which stops the books below inheriting " +
        "the field from further up; 'clear' removes the value so they inherit from the container above again. The result says, book by " +
        "book, which chapters now have what, and which keep their own value or a nearer default, so check with the writer that's what " +
        "they meant. A final (frozen) book's defaults can't be changed, nor a series' or universe's where that would change what a " +
        "final book's chapters have." + WriteHelp)]
    public CallToolResult UpdateChapterDefaults(
        [Description("The hash of the file your edit is based on: 'hash' from get_chapter_defaults (or get_chapter for a book), or from your last write's result.")] string expected_hash,
        [Description("The changes, in order: a list of {op, field, value, index, item}.")] JsonElement changes,
        [Description(UniverseHelp)] string? universe = null,
        [Description("A series' key, to change that series' defaults.")] string? series = null,
        [Description("A book's key, to change that book's defaults.")] string? book = null) =>
        Respond(() => _tools.UpdateChapterDefaults(expected_hash, changes, universe, series, book));

    /// <summary>The <c>checkpoint</c> tool.</summary>
    [McpServerTool(Name = "checkpoint", Title = "Make a checkpoint", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Records the den's files as they are now as a checkpoint (a git commit) with a message, naming you as a co-author. Writes are " +
        "saved to the files straight away; a checkpoint is what makes them part of the history, so make one at the end of a session " +
        "of edits, not after each one. Only the den's files are recorded: all of them that changed, unless 'paths' names some, so a " +
        "checkpoint can include edits the writer made elsewhere. The result lists the recorded files your writes in this session " +
        "saved since they were last recorded ('written_by_session') apart from those that changed some other way " +
        "('changed_outside_session'); a file you wrote that was changed after, say in the writer's editor, is in both. Tell the writer " +
        "about the second list. If nothing changed since the last checkpoint, nothing is recorded. After move_entity, 'paths' must name " +
        "both the old and the new path to record the move. If the den has no history, the result says so ('available' false) and the edits are still saved in the files.")]
    public CallToolResult Checkpoint(
        [Description("What the checkpoint is, such as 'End of chapter 14 session: Lucy's timeline'.")] string message,
        [Description("You, as 'Name <email>' on one line, such as 'Claude <noreply@anthropic.com>'.")] string co_author,
        [Description("Only these files or folders, relative to the den root. Leave it out for the whole den.")] string[]? paths = null) =>
        Respond(() => _tools.Checkpoint(message, co_author, paths));

    /// <summary>
    /// An error result in Wolfden's shape for one the SDK made itself. Every
    /// failure inside a tool comes back from <see cref="Respond{T}"/> as JSON
    /// with a code, so an error result that isn't is the SDK's own: the
    /// arguments didn't bind to the tool's parameters (a missing required
    /// argument, or a value of the wrong type). It becomes <c>bad_argument</c>,
    /// keeping the SDK's message. Any other result passes through.
    /// </summary>
    internal static CallToolResult AsBadArgument(CallToolResult result, string? tool)
    {
        if (result.IsError != true || IsWolfdenError(result))
        {
            return result;
        }

        return BadArgument(tool, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));

        static bool IsWolfdenError(CallToolResult result)
        {
            if (result.Content is not [TextContentBlock { Text: var text }])
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(text);
                return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>A <c>bad_argument</c> error result for arguments that didn't bind, keeping what the SDK said about them.</summary>
    internal static CallToolResult BadArgument(string? tool, string? detail)
    {
        detail = detail?.Trim() ?? "";
        return Respond<object>(() => throw new DenToolException(
            ToolErrorCodes.BadArgument,
            $"The arguments for '{tool}' don't fit its input schema: check that each required argument is given and each has the type the schema " +
            $"says (text, number, or true/false).{(detail.Length == 0 ? "" : $" The server said: {detail}")}"));
    }

    /// <summary>Runs a tool and returns its result as JSON, or its error as an error result.</summary>
    internal static CallToolResult Respond<T>(Func<T> run)
    {
        object result;
        var isError = false;
        try
        {
            result = run()!;
        }
        catch (DenToolException e)
        {
            result = e.ToResult();
            isError = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = new ToolErrorResult(
                ToolJson.Version,
                new ToolError(ToolErrorCodes.Internal, $"Unexpected failure ({e.GetType().Name}: {e.Message}). This is a bug in Wolfden; please report it."));
            isError = true;
        }

        var json = JsonSerializer.SerializeToElement(result, result.GetType(), ToolJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json.GetRawText() }],
            StructuredContent = json,
            IsError = isError,
        };
    }
}
