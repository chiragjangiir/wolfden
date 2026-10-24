using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wolfden.Core.History;

namespace Wolfden.Git;

/// <summary>
/// The git history provider (design.md §6, #19): log, diffs, checkpoints,
/// and tags for one den, through the user's own <c>git</c> command
/// (maintainer decision, 2026-10-22), so their configuration applies:
/// commit signing, hooks, credentials, and <c>safe.directory</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to the den.</b> Git runs in the den's folder, which can be a
/// subfolder of a larger repository, with the den's folder as the
/// pathspec and <c>--relative</c>, so every path in and out is
/// den-relative, and nothing outside the den is listed, compared, or
/// committed.
/// </para>
/// <para>
/// <b>Hidden paths</b> (F-LAY-2), such as <c>.wolfden/</c> or a
/// <c>.gitignore</c>, are left out of every pathspec, so they're never
/// recorded or reported.
/// </para>
/// <para>
/// <b>Checkpoints</b> are <c>git commit --only -- &lt;den paths&gt;</c>:
/// only the den's files go in, and whatever else the user had staged
/// stays staged. New den files are first marked with <c>git add
/// --intent-to-add</c>, which records no content, so <c>--only</c> can
/// commit them.
/// </para>
/// <para>
/// <b>Diffs against the files as they are now</b> include new files git
/// doesn't track yet (but not ignored ones). They're compared through a
/// temporary index, so the user's own index is never changed by a diff.
/// </para>
/// <para>
/// <b>Revisions</b> from callers are resolved to commit hashes first
/// (<c>git rev-parse --verify</c>), and one that starts with <c>-</c> is
/// refused, so a revision can never be read as an option.
/// </para>
/// <para>
/// <b>Known v0 limit:</b> the log of a file is the log of its current path.
/// It doesn't follow the file back through a rename, as <c>git log
/// --follow</c> would, since <c>--follow</c> can't be paged with a
/// <c>before</c> revision once the path changes.
/// </para>
/// </remarks>
public sealed partial class GitHistory : IHistoryProvider
{
    /// <summary>The pathspecs that leave hidden files and folders out, at any depth of the den (F-LAY-2).</summary>
    private static readonly string[] HiddenExcludes = [":(exclude,glob)**/.*", ":(exclude,glob)**/.*/**"];

    /// <summary>The options every diff takes, so the user's diff settings can't change what Wolfden parses.</summary>
    private static readonly string[] DiffOptions =
        ["--no-color", "--no-ext-diff", "--no-textconv", "--relative", "-M", "--src-prefix=a/", "--dst-prefix=b/"];

    // Fields of a log entry: start, separator, end of the header. %B is the raw message.
    private const string LogFormat = "--format=%x01%H%x02%aI%x02%an%x02%B%x03";

    private readonly GitOptions _options;

    private GitHistory(string denRoot, string repositoryRoot, GitOptions options)
    {
        DenRoot = denRoot;
        RepositoryRoot = repositoryRoot;
        _options = options;
    }

    /// <summary>
    /// The history provider for the den at <paramref name="denRoot"/>: git's,
    /// when the den is in a git repository; <see cref="NoHistory"/> when it
    /// isn't; and, when git isn't installed or refuses the repository, a
    /// provider whose every operation says so, in words for the writer.
    /// None of these throws for history that isn't available.
    /// </summary>
    /// <param name="denRoot">The den's folder: the one that holds <c>den.yaml</c>.</param>
    /// <param name="options">Which git to run, or null for the user's own.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public static IHistoryProvider ForDen(string denRoot, GitOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        options ??= GitOptions.Default;
        var root = Path.GetFullPath(denRoot);
        var location = GitRepository.Locate(root, options, cancellationToken);
        return location.Kind switch
        {
            GitLocationKind.InRepository => new GitHistory(root, location.Root!, options),
            GitLocationKind.NotARepository => NoHistory.Instance,
            GitLocationKind.GitNotInstalled => new UnavailableGit(
                "Git isn't installed, or Wolfden can't find it on the PATH",
                "Install git to keep the den's history (the den must be in a git repository that isn't in a synced folder)."),
            _ => new UnavailableGit(
                $"Git can't use the repository the den is in ({location.Problem})",
                "Fix what git says, then try again."),
        };
    }

    /// <summary>The den's folder, as a full path.</summary>
    public string DenRoot { get; }

    /// <summary>The top folder of the repository the den is in, which may be the den's own folder or one above it.</summary>
    public string RepositoryRoot { get; }

    /// <inheritdoc />
    public string Name => "git";

    /// <inheritdoc />
    public HistoryCapabilities Capabilities => HistoryCapabilities.All;

    /// <inheritdoc />
    public HistoryResult<IReadOnlyList<HistoryEntry>> Log(HistoryLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "The limit must be at least 1.");
        }

        var specs = Pathspecs(query.Path is null ? null : [query.Path]);
        var head = Head(cancellationToken);
        var skip = 0;
        if (query.Before is not null)
        {
            var before = Resolve(query.Before, cancellationToken);
            if (head is null)
            {
                throw NotInLog(query.Before);
            }

            // Every page walks the same log, from HEAD with the same path
            // filter, and starts after 'before' in it. 'git log <before>'
            // would list only its ancestors: after a page that ends on a
            // merged branch's commit, the main line's older commits would
            // be lost.
            var ids = Git(["log", "--format=%H", head, "--", .. specs], cancellationToken)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var at = Array.IndexOf(ids, before);
            if (at < 0)
            {
                throw NotInLog(query.Before);
            }

            skip = at + 1;
        }

        if (head is null)
        {
            // Nothing has been committed yet.
            return HistoryResult<IReadOnlyList<HistoryEntry>>.Done([]);
        }

        return HistoryResult<IReadOnlyList<HistoryEntry>>.Done(ReadLog(head, query.Limit, specs, cancellationToken, skip));

        static HistoryRevisionException NotInLog(string revision) => new(
            revision,
            $"'{revision}' isn't one of the checkpoints in this history. For the next page, pass the id of the last entry of the page before, with the same path.");
    }

    /// <inheritdoc />
    public HistoryResult<HistoryDiff> Diff(HistoryDiffQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var specs = Pathspecs(query.Path is null ? null : [query.Path]);
        string from;
        if (query.From is null)
        {
            if (LatestCheckpoint(cancellationToken) is not { } latest)
            {
                return HistoryResult<HistoryDiff>.Unavailable(
                    "Nothing in the den has been checkpointed yet, so there's no earlier version to compare with. Make a checkpoint first.");
            }

            from = latest;
        }
        else
        {
            from = Resolve(query.From, cancellationToken);
        }

        if (query.To is not null)
        {
            var to = Resolve(query.To, cancellationToken);
            return HistoryResult<HistoryDiff>.Done(new HistoryDiff(from, to, CompareFiles([from, to], specs, null, cancellationToken)));
        }

        // The files as they are now, new ones included: put them in a
        // temporary index and compare that, leaving the user's index alone.
        var index = Path.Combine(Path.GetTempPath(), $"wolfden-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = index };
        try
        {
            Git(["read-tree", Head(cancellationToken) ?? "--empty"], cancellationToken, environment: environment);
            // The whole den, so a path that's gone, or never was, is still compared.
            Git(["add", "--all", "--", .. Pathspecs(null)], cancellationToken, environment: environment);
            return HistoryResult<HistoryDiff>.Done(new HistoryDiff(from, null, CompareFiles(["--cached", from], specs, environment, cancellationToken)));
        }
        finally
        {
            Delete(index);
            Delete(index + ".lock");
        }
    }

    /// <inheritdoc />
    public HistoryResult<HistoryEntry?> Checkpoint(CheckpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message, nameof(request));
        if (request.Message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException("A checkpoint's message can't hold control characters other than line breaks and tabs.", nameof(request));
        }

        foreach (var coAuthor in request.CoAuthors ?? [])
        {
            if (!CoAuthorPattern().IsMatch(coAuthor))
            {
                throw new ArgumentException($"'{coAuthor}' isn't a co-author: write it as 'Name <email>', on one line.", nameof(request));
            }
        }

        var specs = Pathspecs(request.Paths);
        var head = Head(cancellationToken);

        // New den files (not ignored ones) are made known to the index
        // without their content, so 'commit --only' takes them; nothing else
        // is staged.
        var untracked = Git(["ls-files", "-z", "--others", "--exclude-standard", "--", .. specs], cancellationToken);
        if (untracked.Length > 0)
        {
            // Exactly the files listed, taken literally: 'git add' with an
            // exclude pathspec skips a file named inside an untracked folder.
            Git(
                ["--literal-pathspecs", "add", "--intent-to-add", "--pathspec-from-file=-", "--pathspec-file-nul"],
                cancellationToken,
                input: untracked);
        }
        else if (!HasChanges(head, specs, cancellationToken))
        {
            return HistoryResult<HistoryEntry?>.Done(null);
        }

        var message = new StringBuilder(request.Message.Trim().ReplaceLineEndings("\n")).Append('\n');
        if (request.CoAuthors is { Count: > 0 } coAuthors)
        {
            message.Append('\n');
            foreach (var coAuthor in coAuthors)
            {
                message.Append("Co-Authored-By: ").Append(coAuthor.Trim()).Append('\n');
            }
        }

        GitRun commit;
        try
        {
            commit = Run(["commit", "--only", "--quiet", "--file=-", "--", .. specs], cancellationToken, input: message.ToString());
        }
        catch (OperationCanceledException)
        {
            // Git may have made the commit before it was stopped (say, in a
            // slow post-commit hook). Then the checkpoint was recorded, and
            // taking the marks off would stage the new files' deletion.
            if (Head(CancellationToken.None) == head)
            {
                Unmark(untracked);
            }

            throw;
        }

        if (!commit.Succeeded)
        {
            // Put the new files back as they were: untracked, not marked.
            Unmark(untracked);
            throw new HistoryException(
                $"Git refused the checkpoint, so nothing was recorded ({commit.Said}). A pre-commit or commit-msg hook, commit signing, " +
                "or a merge or rebase in progress can do this. The den's files and what was staged are as they were.");
        }

        var id = Head(cancellationToken) ?? throw new HistoryException("Git made the checkpoint, but the repository has no commit afterwards.");
        var entry = ReadLog(id, 1, Pathspecs(null), cancellationToken).SingleOrDefault()
            ?? throw new HistoryException($"Git made the checkpoint {id}, but it doesn't change the den.");
        return HistoryResult<HistoryEntry?>.Done(entry);
    }

    /// <summary>
    /// Takes the intent-to-add marks off <paramref name="untracked"/> (git's
    /// <c>-z</c> list of files that were untracked), after a checkpoint that
    /// didn't commit, so they're untracked again. Best effort: a failure here
    /// mustn't hide why the checkpoint failed.
    /// </summary>
    private void Unmark(string untracked)
    {
        if (untracked.Length == 0)
        {
            return;
        }

        try
        {
            Run(
                // --force: the entries hold no content, and the files stay where they are.
                ["--literal-pathspecs", "rm", "--cached", "--force", "--quiet", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul"],
                CancellationToken.None,
                input: untracked);
        }
        catch (HistoryException)
        {
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A tag that already exists is never moved or replaced: the request
    /// fails with a <see cref="HistoryException"/> that says so. Tags belong
    /// to the whole repository, so another den in it may hold the name.
    /// With a message, the tag is annotated (and signed, if the user's git
    /// signs tags); without one, it's a lightweight tag.
    /// </remarks>
    public HistoryResult<HistoryTag> Tag(TagRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckTagName(request.Name, cancellationToken);
        if (Run(["rev-parse", "--verify", "--quiet", $"refs/tags/{request.Name}"], cancellationToken).Succeeded)
        {
            throw new HistoryException(
                $"The repository already has a tag '{request.Name}', so Wolfden didn't make it again: it never moves or replaces a tag. " +
                "Tags belong to the whole repository, so another den in it may have made it.");
        }

        string at;
        if (request.At is null)
        {
            if (LatestCheckpoint(cancellationToken) is not { } latest)
            {
                return HistoryResult<HistoryTag>.Unavailable("Nothing in the den has been checkpointed yet, so there's nothing to tag. Make a checkpoint first.");
            }

            at = latest;
        }
        else
        {
            at = Resolve(request.At, cancellationToken);
        }

        if (request.Message is null)
        {
            // A lightweight tag can't be signed: with tag.gpgSign, git would
            // make an annotated tag and ask an editor for its message.
            Git(["-c", "tag.gpgSign=false", "tag", request.Name, at], cancellationToken);
        }
        else
        {
            Git(["tag", "--file=-", request.Name, at], cancellationToken, input: request.Message.ReplaceLineEndings("\n") + "\n");
        }

        return HistoryResult<HistoryTag>.Done(new HistoryTag(request.Name, at));
    }

    /// <inheritdoc />
    public HistoryResult<IReadOnlyList<HistoryTag>> Tags(string? prefix = null, CancellationToken cancellationToken = default)
    {
        var output = Git(["for-each-ref", "--format=%(refname)%00%(objectname)%00%(*objectname)", "refs/tags/"], cancellationToken);
        var tags = new List<HistoryTag>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length < 3 || !parts[0].StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                continue;
            }

            var name = parts[0]["refs/tags/".Length..];
            if (prefix is null || name.StartsWith(prefix, StringComparison.Ordinal))
            {
                // An annotated tag names a tag object; the revision is the commit it points at.
                tags.Add(new HistoryTag(name, parts[2].Length > 0 ? parts[2] : parts[1]));
            }
        }

        tags.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return HistoryResult<IReadOnlyList<HistoryTag>>.Done(tags);
    }

    /// <summary>The newest commit that changed the den, or null if none has.</summary>
    private string? LatestCheckpoint(CancellationToken cancellationToken)
    {
        if (Head(cancellationToken) is not { } head)
        {
            return null;
        }

        var id = Git(["log", "--max-count=1", "--format=%H", head, "--", .. Pathspecs(null)], cancellationToken).Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>The commit HEAD names, or null in a repository with no commits yet.</summary>
    private string? Head(CancellationToken cancellationToken)
    {
        var run = Run(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken);
        return run.Succeeded ? run.Output.Trim() : null;
    }

    /// <summary>A caller's revision as a commit hash.</summary>
    private string Resolve(string revision, CancellationToken cancellationToken)
    {
        if (revision.Length == 0 || revision.StartsWith('-') || revision.Any(char.IsControl) || revision.Contains(' ', StringComparison.Ordinal))
        {
            throw new HistoryRevisionException(revision, $"'{revision}' isn't a checkpoint: give an id from the history, or a tag's name.");
        }

        var run = Run(["rev-parse", "--verify", "--quiet", revision + "^{commit}"], cancellationToken);
        return run.Succeeded
            ? run.Output.Trim()
            : throw new HistoryRevisionException(revision, $"There's no checkpoint '{revision}' in the repository: give an id from the history, or a tag's name.");
    }

    private void CheckTagName(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // 'HEAD', '@', and 'refs/...' are valid under refs/tags/, but would be read as something else.
        if (name.StartsWith('-') || name is "HEAD" or "@" || name.StartsWith("refs/", StringComparison.Ordinal)
            || !Run(["check-ref-format", $"refs/tags/{name}"], cancellationToken).Succeeded)
        {
            throw new ArgumentException($"'{name}' can't be a git tag's name.", nameof(name));
        }
    }

    /// <summary>True if the tracked den files at <paramref name="specs"/> differ from HEAD.</summary>
    private bool HasChanges(string? head, IReadOnlyList<string> specs, CancellationToken cancellationToken)
    {
        if (head is null)
        {
            return Git(["ls-files", "-z", "--", .. specs], cancellationToken).Length > 0;
        }

        var run = Run(["diff", "--quiet", "--no-ext-diff", "--no-textconv", head, "--", .. specs], cancellationToken);
        return run.ExitCode switch
        {
            0 => false,
            1 => true,
            _ => throw Failed("diff", run),
        };
    }

    /// <summary>The log from <paramref name="start"/> of the commits that change <paramref name="specs"/>, newest first.</summary>
    private List<HistoryEntry> ReadLog(string start, int? count, IReadOnlyList<string> specs, CancellationToken cancellationToken, int skip = 0)
    {
        List<string> args =
        [
            "--no-optional-locks", "log", "--no-color", "--no-show-signature", "--encoding=UTF-8", "--relative", "-z", "--name-status", "-M",
            "--diff-merges=first-parent", LogFormat,
        ];
        if (skip > 0)
        {
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--skip={skip}"));
        }

        if (count is { } n)
        {
            args.Add(string.Create(CultureInfo.InvariantCulture, $"--max-count={n}"));
        }

        args.Add(start);
        args.Add("--");
        args.AddRange(specs);

        var entries = new List<HistoryEntry>();
        foreach (var chunk in Git(args, cancellationToken).Split('\x01', StringSplitOptions.RemoveEmptyEntries))
        {
            var end = chunk.IndexOf('\x03', StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var header = chunk[..end].Split('\x02', 4);
            if (header.Length < 4)
            {
                throw new HistoryException("Git's log didn't have the shape Wolfden asked for.");
            }

            var changes = ParseChanges(chunk[(end + 1)..].Split('\0'), raw: false).Select(c => c.Change).OfType<HistoryFileChange>().ToList();
            entries.Add(new HistoryEntry(
                header[0],
                DateTimeOffset.Parse(header[1], CultureInfo.InvariantCulture),
                header[2],
                header[3].ReplaceLineEndings("\n").TrimEnd('\n'),
                changes));
        }

        return entries;
    }

    /// <summary>The den files that differ between <paramref name="revisions"/>, each with its patch.</summary>
    private List<HistoryFileDiff> CompareFiles(
        IReadOnlyList<string> revisions, IReadOnlyList<string> specs, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        var raw = Git(["diff", .. DiffOptions, "--raw", "-z", .. revisions, "--", .. specs], cancellationToken, environment: environment);
        var patch = Git(["diff", .. DiffOptions, "--patch", .. revisions, "--", .. specs], cancellationToken, environment: environment);
        var changes = ParseChanges(raw.Split('\0'), raw: true);
        var patches = SplitPatch(patch);
        if (patches.Count != changes.Count)
        {
            throw new HistoryException($"Git's diff listed {changes.Count} files but gave {patches.Count} patches.");
        }

        return changes
            .Select((c, i) => c.Change is null ? null : new HistoryFileDiff(c.Change, patches[i]))
            .OfType<HistoryFileDiff>()
            .ToList();
    }

    /// <summary>
    /// Changes from git's <c>-z</c> name-status or raw output, in order. A
    /// change to a hidden path has a null change, so lists stay in step with
    /// the patches; a rename from or to a hidden path is an add or a delete.
    /// </summary>
    private static List<(HistoryFileChange? Change, string Status)> ParseChanges(string[] tokens, bool raw)
    {
        var changes = new List<(HistoryFileChange?, string)>();
        for (var i = 0; i < tokens.Length;)
        {
            var token = tokens[i].Trim('\n', '\r');
            if (token.Length == 0)
            {
                i++;
                continue;
            }

            // Raw lines are ':<old mode> <new mode> <old id> <new id> <status>'.
            var status = raw ? token[(token.LastIndexOf(' ') + 1)..] : token;
            if (raw && !token.StartsWith(':'))
            {
                throw new HistoryException("Git's diff didn't have the shape Wolfden asked for.");
            }

            var two = status[0] is 'R' or 'C';
            if (i + (two ? 2 : 1) >= tokens.Length)
            {
                throw new HistoryException("Git's list of changed files ended part way.");
            }

            var path = tokens[i + 1];
            var newPath = two ? tokens[i + 2] : null;
            i += two ? 3 : 2;
            changes.Add((Change(status[0], path, newPath), status));
        }

        return changes;

        static HistoryFileChange? Change(char status, string path, string? newPath)
        {
            if (newPath is not null)
            {
                return (IsHidden(path), IsHidden(newPath), status) switch
                {
                    (_, true, 'C') => null,
                    (_, false, 'C') => new HistoryFileChange(HistoryChangeKind.Added, newPath),
                    (true, true, _) => null,
                    (true, false, _) => new HistoryFileChange(HistoryChangeKind.Added, newPath),
                    (false, true, _) => new HistoryFileChange(HistoryChangeKind.Deleted, path),
                    _ => new HistoryFileChange(HistoryChangeKind.Renamed, newPath, path),
                };
            }

            if (IsHidden(path))
            {
                return null;
            }

            return status switch
            {
                'A' => new HistoryFileChange(HistoryChangeKind.Added, path),
                'D' => new HistoryFileChange(HistoryChangeKind.Deleted, path),
                _ => new HistoryFileChange(HistoryChangeKind.Modified, path),
            };
        }
    }

    /// <summary>A patch split into one part per file, each starting at its <c>diff --git</c> line.</summary>
    private static List<string> SplitPatch(string patch)
    {
        var parts = new List<string>();
        StringBuilder? part = null;
        foreach (var line in patch.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (part is not null)
                {
                    parts.Add(part.ToString());
                }

                part = new StringBuilder();
            }

            part?.Append(line).Append('\n');
        }

        if (part is not null)
        {
            parts.Add(part.ToString().TrimEnd('\n') + "\n");
        }

        return parts;
    }

    /// <summary>
    /// The pathspecs for den-relative <paramref name="paths"/>, or for the
    /// whole den, always without hidden paths. Each path is taken literally,
    /// so a name with <c>*</c> or <c>[</c> in it is just a name.
    /// </summary>
    private static List<string> Pathspecs(IReadOnlyList<string>? paths)
    {
        var specs = new List<string>();
        if (paths is null || paths.Count == 0)
        {
            specs.Add(".");
        }
        else
        {
            foreach (var path in paths)
            {
                specs.Add(":(literal)" + CheckPath(path));
            }
        }

        specs.AddRange(HiddenExcludes);
        return specs;
    }

    /// <summary>A den-relative path, without a trailing <c>/</c>, or an <see cref="ArgumentException"/> if it isn't one.</summary>
    private static string CheckPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0 || trimmed.StartsWith('/') || trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains(':', StringComparison.Ordinal)
            || trimmed.Split('/').Any(name => name.Length == 0 || name.StartsWith('.')))
        {
            throw new ArgumentException(
                $"'{path}' isn't a path in the den: give one relative to the den's folder, with '/' between names, and nothing hidden (no name starting with '.').",
                nameof(path));
        }

        return trimmed;
    }

    private static bool IsHidden(string path) => path.Split('/').Any(name => name.StartsWith('.'));

    /// <summary>Runs git in the den's folder and returns its output, or throws a <see cref="HistoryException"/> if it fails.</summary>
    private string Git(
        IReadOnlyList<string> args, CancellationToken cancellationToken, string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var run = Run(args, cancellationToken, input, environment);
        return run.Succeeded ? run.Output : throw Failed(CommandName(args), run);
    }

    /// <summary>The git command in <paramref name="args"/>: the first argument that isn't an option, or the value of <c>-c</c>.</summary>
    private static string CommandName(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-c")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }

        return "command";
    }

    private GitRun Run(
        IReadOnlyList<string> args, CancellationToken cancellationToken, string? input = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        try
        {
            return GitProcess.Run(_options, DenRoot, args, input, environment, cancellationToken);
        }
        catch (GitNotFoundException e)
        {
            throw new HistoryException($"Git couldn't be run ({e.Message}).", e);
        }
    }

    private static HistoryException Failed(string command, GitRun run) => new($"git {command} failed: {run.Said}");

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary file; the OS cleans its temp folder.
        }
    }

    [GeneratedRegex(@"^[^<>\r\n]*[^<>\s][^<>\r\n]* <[^<>\s]+>$")]
    private static partial Regex CoAuthorPattern();
}
