using System.Collections.Concurrent;
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
/// temporary index, so the user's own index is never changed by a diff,
/// and git compares the files on disk without storing them, so a diff
/// writes nothing into the repository's objects (#142). Clean filters still
/// run on the files compared, and Git LFS's stores what it cleans in
/// <c>.git/lfs/objects</c>. In a sparse checkout, files marked skip-worktree
/// (the ones it keeps off the disk, or any marked by hand) count as
/// unchanged; without one, a file marked by hand is compared as it is on
/// disk. Edits inside a submodule aren't the den's.
/// </para>
/// <para>
/// <b>Revisions</b> from callers are resolved to commit hashes first
/// (<c>git rev-parse --verify</c>), and one that starts with <c>-</c> is
/// refused, so a revision can never be read as an option.
/// </para>
/// <para>
/// <b>Cancelling and time limits</b> (#143, #145). A git command that only
/// reads is stopped when it's cancelled or runs past
/// <see cref="GitOptions.ReadTimeout"/>. One that writes the user's index
/// or refs (marking new files, the commit, a tag) is never stopped once
/// started, since git stopped part way leaves <c>.git/index.lock</c>
/// behind and locks the whole repository. A checkpoint can be cancelled
/// only up to its commit; the commit runs to the end, and a checkpoint it
/// records is returned. Past <see cref="GitOptions.WriteTimeout"/>,
/// Wolfden stops waiting, leaves git running, and throws a
/// <see cref="HistoryException"/> that says so; if git then ends without
/// committing, the new files' marks are taken off. Wolfden never deletes
/// git's lock files. Checkpoints in one repository take turns within this
/// process, and that late rollback waits its turn, so it can't take marks
/// off files a newer checkpoint is committing; another process
/// checkpointing the same repository at that moment isn't covered.
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

    /// <summary>
    /// The options every diff takes, so the user's diff settings can't change
    /// what Wolfden parses. A submodule counts as changed only when the
    /// commit it's at does: edits inside it aren't the den's (#142 review, L2).
    /// <c>--submodule=short</c> keeps a submodule's patch a <c>diff --git</c>
    /// part whatever <c>diff.submodule</c> says (#158 review, M1).
    /// </summary>
    private static readonly string[] DiffOptions =
    [
        "--no-color", "--no-ext-diff", "--no-textconv", "--relative", "-M", "--src-prefix=a/", "--dst-prefix=b/", "--ignore-submodules=dirty",
        "--submodule=short",
    ];

    /// <summary>
    /// Settings for every git command that uses Wolfden's temporary index
    /// (#142 review):
    /// <list type="bullet">
    /// <item><c>core.ignoreStat=false</c>: otherwise git marks the entries
    /// it adds as unchanged, and then trusts their recorded content (the
    /// empty blob, for a new file) instead of reading the file.</item>
    /// <item><c>core.splitIndex=false</c>: otherwise git splits the
    /// temporary index and leaves the shared part in the user's
    /// <c>.git</c> folder.</item>
    /// <item><c>diff.autoRefreshIndex=true</c>: comparing with the files on
    /// disk, git drops a file whose stat data doesn't match but whose
    /// content does only while this is on (its default), so a user who
    /// turned it off would see unchanged files as modified.</item>
    /// </list>
    /// </summary>
    private static readonly string[] TempIndexSettings =
        ["-c", "core.ignoreStat=false", "-c", "core.splitIndex=false", "-c", "diff.autoRefreshIndex=true"];

    /// <summary>The empty blob's id with SHA-1, and with SHA-256: what an entry for a new file names in the temporary index.</summary>
    private const string EmptyBlobSha1 = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";

    private const string EmptyBlobSha256 = "473a0f4c3be8a93681a267e3b1e9a7dcda1185436fe141f7749120a303721813";

    /// <summary>A submodule's mode in git's raw output.</summary>
    private const string GitlinkMode = "160000";

    /// <summary>The mode in git's raw output of a side that isn't there: an add's old side, or a delete's new one.</summary>
    private const string NoMode = "000000";

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

        // The files as they are now, new ones included (#142): a temporary
        // index lists HEAD's files and the new ones, and git compares the
        // files on disk with 'from' through it. The user's index is left
        // alone, and nothing is written into the repository's objects.
        var index = Path.Combine(Path.GetTempPath(), $"wolfden-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = index };
        try
        {
            Git([.. TempIndexSettings, "read-tree", Head(cancellationToken) ?? "--empty"], cancellationToken, environment: environment);
            KeepSparseCheckout(environment, cancellationToken);

            // read-tree leaves no stat data, so git would read every den file
            // to tell which ones changed. Refreshing reads them once, records
            // which are as committed, and writes only the temporary index.
            // It fails if no file in the index matches the pathspecs, and
            // then there's nothing to refresh.
            _ = Run([.. TempIndexSettings, "add", "--refresh", "--", .. specs], cancellationToken, environment: environment);
            ListNewFiles(specs, from.Length == 64 ? EmptyBlobSha256 : EmptyBlobSha1, environment, cancellationToken);
            return HistoryResult<HistoryDiff>.Done(new HistoryDiff(from, null, CompareFiles([from], specs, environment, cancellationToken)));
        }
        finally
        {
            Delete(index);
            Delete(index + ".lock");
        }
    }

    /// <summary>
    /// In a sparse checkout, marks the den files the user's index marks
    /// skip-worktree (the ones the sparse checkout leaves off the disk, or
    /// any marked by hand) the same way in the temporary index, so they're
    /// taken as recorded, not as deleted (#142 review, L1). Without a sparse
    /// checkout, nothing is read: a file marked by hand is compared as it
    /// is on disk.
    /// </summary>
    private void KeepSparseCheckout(IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var sparse = Run(["config", "--bool", "core.sparseCheckout"], cancellationToken);
        if (!sparse.Succeeded || OutputOf(sparse, "config").Trim() != "true")
        {
            return;
        }

        // The user's index: 'S <path>' is a skip-worktree file, and 'S
        // <folder>/' a folder a sparse index keeps as one entry. Paths are
        // den-relative. With --sparse (git 2.35 and later), git doesn't
        // expand a sparse index, which can write a tree into the user's
        // objects; and the whole den is listed, since a path in a folder
        // entry doesn't match it.
        var listed = Run(["ls-files", "-z", "-t", "--sparse", "--", .. Pathspecs(null)], cancellationToken);
        if (!listed.Succeeded)
        {
            listed = Run(["ls-files", "-z", "-t", "--", .. Pathspecs(null)], cancellationToken);
        }

        var skipped = (listed.Succeeded ? OutputOf(listed, "ls-files") : throw Failed("ls-files", listed))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith("S ", StringComparison.Ordinal))
            .Select(entry => entry[2..])
            .ToList();
        if (skipped.Count == 0)
        {
            return;
        }

        var files = skipped.Where(path => !path.EndsWith('/')).ToHashSet(StringComparer.Ordinal);
        var folders = skipped.Where(path => path.EndsWith('/')).ToList();

        // Only paths the temporary index has: update-index refuses one that
        // isn't in the index. The whole den, as above.
        var marks = new StringBuilder();
        foreach (var path in Git([.. TempIndexSettings, "ls-files", "-z", "--", .. Pathspecs(null)], cancellationToken, environment: environment)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (files.Contains(path) || folders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal)))
            {
                marks.Append(path).Append('\0');
            }
        }

        if (marks.Length > 0)
        {
            Git([.. TempIndexSettings, "update-index", "-z", "--skip-worktree", "--stdin"], cancellationToken, input: marks.ToString(), environment: environment);
        }
    }

    /// <summary>
    /// Adds the den's new files at <paramref name="specs"/> (untracked, not
    /// ignored) to the temporary index <paramref name="environment"/> names,
    /// without their content, so a diff against the files on disk includes
    /// them.
    /// </summary>
    /// <param name="specs">The pathspecs.</param>
    /// <param name="emptyBlob">The empty blob's id, in the repository's hash.</param>
    /// <param name="environment">Names the temporary index.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <remarks>
    /// Each entry names the empty blob and has no stat data, and
    /// <see cref="TempIndexSettings"/> keeps git from marking it unchanged,
    /// so git never takes it as up to date and always reads the file itself;
    /// the empty blob is never read, so it needn't exist. That's the shape
    /// <c>git add --intent-to-add</c> gives an entry, but <c>add</c> also
    /// writes the empty blob into the repository's objects, and
    /// <c>update-index --index-info</c> writes nothing there.
    /// </remarks>
    private void ListNewFiles(IReadOnlyList<string> specs, string emptyBlob, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        // Listed against the temporary index, so a file the user has staged
        // but not committed counts as new. Repository-relative, as
        // update-index takes them.
        var untracked = Git(
                [.. TempIndexSettings, "ls-files", "-z", "--full-name", "--others", "--exclude-standard", "--", .. specs], cancellationToken, environment: environment)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            // A folder (ending in '/') is a nested repository, not a den file.
            .Where(path => !path.EndsWith('/'))
            .ToList();
        if (untracked.Count == 0)
        {
            return;
        }

        var entries = new StringBuilder();
        foreach (var path in untracked)
        {
            entries.Append("100644 ").Append(emptyBlob).Append('\t').Append(path).Append('\0');
        }

        Git([.. TempIndexSettings, "update-index", "-z", "--add", "--index-info"], cancellationToken, input: entries.ToString(), environment: environment);
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

        // One checkpoint at a time in a repository, and none while a rollback
        // from one Wolfden stopped waiting for is still to run (UnmarkWhenDone).
        var gate = Gate();
        gate.Wait(cancellationToken);
        try
        {
            return CheckpointInGate(request, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private HistoryResult<HistoryEntry?> CheckpointInGate(CheckpointRequest request, CancellationToken cancellationToken)
    {
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
            GitRun marked;
            try
            {
                marked = Start(
                    ["--literal-pathspecs", "add", "--intent-to-add", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    cancellationToken,
                    input: untracked,
                    kind: GitCallKind.MustFinish);
            }
            catch (GitTimeoutException e) when (e.StillRunning is { } running)
            {
                // No commit follows, so once git has marked them, take the marks off.
                UnmarkWhenDone(running, head, untracked, onlyIfFailed: false);
                throw TimedOut(e);
            }

            if (!marked.Succeeded)
            {
                throw Failed("add", marked);
            }
        }
        else if (!HasChanges(head, specs, cancellationToken))
        {
            return HistoryResult<HistoryEntry?>.Done(null);
        }

        var message = new StringBuilder(LineFeeds(request.Message.Trim())).Append('\n');
        if (request.CoAuthors is { Count: > 0 } coAuthors)
        {
            message.Append('\n');
            foreach (var coAuthor in coAuthors)
            {
                message.Append("Co-Authored-By: ").Append(coAuthor.Trim()).Append('\n');
            }
        }

        // The last point a checkpoint can be cancelled. The commit itself
        // can't be (#145): git stopped part way would leave index.lock
        // behind, which locks the whole repository and keeps the marks from
        // being taken off.
        if (cancellationToken.IsCancellationRequested)
        {
            Unmark(untracked);
            cancellationToken.ThrowIfCancellationRequested();
        }

        GitRun commit;
        try
        {
            commit = Start(
                ["commit", "--only", "--quiet", "--file=-", "--", .. specs], CancellationToken.None, input: message.ToString(), kind: GitCallKind.MustFinish);
        }
        catch (GitTimeoutException e) when (e.StillRunning is { } running)
        {
            // Git may yet make the commit. If it doesn't, put the new files
            // back as they were once it's done; if it does, taking the marks
            // off would stage the new files' deletion.
            UnmarkWhenDone(running, head, untracked, onlyIfFailed: true);
            throw new HistoryException(
                $"Git hasn't finished the checkpoint after {GitProcess.Describe(e.Timeout)}. Wolfden stopped waiting but left git running, " +
                "since stopping it part way would leave the repository locked. When git finishes, it may still record the checkpoint, " +
                $"so look at the history before trying again. {LikelyCauses} If git ends without recording it, what was staged is put back as it was.",
                e);
        }

        if (!commit.Succeeded)
        {
            // Put the new files back as they were: untracked, not marked.
            Unmark(untracked);
            throw new HistoryException(
                $"Git refused the checkpoint, so nothing was recorded ({commit.Said}). A pre-commit or commit-msg hook, commit signing, " +
                "or a merge or rebase in progress can do this. The den's files and what was staged are as they were.");
        }

        // The checkpoint is recorded, so the caller hears about it even if
        // it was cancelled while git was committing.
        string? id;
        HistoryEntry? entry;
        try
        {
            id = Head(CancellationToken.None);
            entry = id is null ? null : ReadLog(id, 1, Pathspecs(null), CancellationToken.None).SingleOrDefault();
        }
        catch (HistoryException e)
        {
            var why = e.InnerException is GitTimeoutException timedOut ? timedOut.Message : e.Message.TrimEnd('.');
            throw new HistoryException(
                $"Git recorded the checkpoint, but Wolfden couldn't read it back ({why}). It's in the history, so don't make it again.", e);
        }

        if (id is null)
        {
            throw new HistoryException("Git made the checkpoint, but the repository has no commit afterwards.");
        }

        return HistoryResult<HistoryEntry?>.Done(entry ?? throw new HistoryException($"Git made the checkpoint {id}, but it doesn't change the den."));
    }

    /// <summary>What a git that writes may be waiting on, for a message (#143).</summary>
    private const string LikelyCauses =
        "Git may be waiting on signing (a passphrase prompt), a slow or stuck hook, a credential prompt, " +
        "or its automatic maintenance, which on Windows runs before git returns.";

    /// <summary>
    /// Checkpoints in one repository, from any <see cref="GitHistory"/> in
    /// this process, take turns (#145 review, L4). It never shrinks, on
    /// purpose: it holds one small semaphore per repository a process has
    /// checkpointed, which is a handful, and removing one safely while
    /// another thread may be about to wait on it isn't worth the risk.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private SemaphoreSlim Gate() => Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(RepositoryRoot), _ => new SemaphoreSlim(1, 1));

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
                input: untracked,
                kind: GitCallKind.MustFinish);
        }
        catch (HistoryException)
        {
        }
    }

    /// <summary>
    /// Once <paramref name="running"/> (a git command Wolfden stopped waiting
    /// for) has exited, takes the marks off <paramref name="untracked"/> if no
    /// checkpoint was made: HEAD is still <paramref name="head"/>, and, with
    /// <paramref name="onlyIfFailed"/>, git failed. Lock files are never
    /// touched: until git exits, it holds them.
    /// </summary>
    private void UnmarkWhenDone(Task<GitRun> running, string? head, string untracked, bool onlyIfFailed)
    {
        if (untracked.Length == 0)
        {
            return;
        }

        _ = running.ContinueWith(
            done =>
            {
                if (onlyIfFailed && done.Result.Succeeded)
                {
                    return;
                }

                // Not while another checkpoint is under way: it may have
                // marked the same files, and taking its marks off would leave
                // them out of its commit without a word.
                var gate = Gate();
                gate.Wait();
                try
                {
                    if (Head(CancellationToken.None) == head)
                    {
                        Unmark(untracked);
                    }
                }
                catch (HistoryException)
                {
                    // Best effort, with no one left to tell.
                }
                finally
                {
                    gate.Release();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
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

        // A lightweight tag can't be signed: with tag.gpgSign, git would
        // make an annotated tag and ask an editor for its message.
        string[] args = request.Message is null
            ? ["-c", "tag.gpgSign=false", "tag", request.Name, at]
            : ["tag", "--file=-", request.Name, at];
        GitRun made;
        try
        {
            made = Start(args, cancellationToken, input: request.Message is null ? null : LineFeeds(request.Message) + "\n", kind: GitCallKind.MustFinish);
        }
        catch (GitTimeoutException e)
        {
            throw TimedOut(e, $"Git may still make the tag '{request.Name}' when it finishes, so check whether it exists before trying again.");
        }

        if (!made.Succeeded)
        {
            throw Failed("tag", made);
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

    /// <inheritdoc />
    /// <remarks>
    /// Compares git's object ids: the blob HEAD has at the path, and the one
    /// <c>git hash-object --path</c> makes of the bytes, with the same
    /// attributes and filters (line endings, say) a commit would apply.
    /// </remarks>
    public HistoryResult<bool> HoldsAtLatest(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var spec = CheckPath(path);
        if (Head(cancellationToken) is not { } head)
        {
            return HistoryResult<bool>.Done(false);
        }

        var at = Run(["rev-parse", "--verify", "--quiet", $"{head}:./{spec}"], cancellationToken);
        if (!at.Succeeded)
        {
            return HistoryResult<bool>.Done(false);
        }

        var recorded = OutputOf(at, "rev-parse").Trim();
        var mine = Git(["hash-object", "--stdin", $"--path={spec}"], cancellationToken, input: Encoding.UTF8.GetString(content)).Trim();
        return HistoryResult<bool>.Done(recorded.Length > 0 && recorded == mine);
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
        return run.Succeeded ? CommitId(run) : null;
    }

    /// <summary>The commit hash a successful <c>rev-parse --verify</c> printed; anything else is git's output gone wrong, never a hash to use.</summary>
    private static string CommitId(GitRun run)
    {
        var id = OutputOf(run, "rev-parse").Trim();
        return id.Length >= 40 && id.All(char.IsAsciiHexDigitLower)
            ? id
            : throw new HistoryException($"git rev-parse didn't give a commit id (it gave '{id}').");
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
            ? CommitId(run)
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
            "--no-optional-locks", "log", "--no-color", "--no-show-signature", "--encoding=UTF-8", "--relative", "-z", "--raw", "-M",
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

            var changes = ParseChanges(chunk[(end + 1)..].Split('\0')).Select(c => c.Change).OfType<HistoryFileChange>().ToList();
            entries.Add(new HistoryEntry(
                header[0],
                DateTimeOffset.Parse(header[1], CultureInfo.InvariantCulture),
                header[2],
                LineFeeds(header[3]).TrimEnd('\n'),
                changes));
        }

        return entries;
    }

    /// <summary>The den files that differ between <paramref name="revisions"/>, each with its patch.</summary>
    /// <remarks>
    /// One git run gives the list and the patches (#142 review, H2):
    /// comparing with the files on disk, two runs could each see a file
    /// saved in between differently. Each patch is matched to its file by
    /// its <c>diff --git</c> line, never by position alone.
    /// </remarks>
    private List<HistoryFileDiff> CompareFiles(
        IReadOnlyList<string> revisions, IReadOnlyList<string> specs, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        var output = Git(
            [.. TempIndexSettings, "diff", .. DiffOptions, "--raw", "--patch", "-z", .. revisions, "--", .. specs], cancellationToken, environment: environment);
        return ReadDiff(output);
    }

    /// <summary>
    /// The den files and their patches from the output of one
    /// <c>git diff --raw --patch -z</c>.
    /// </summary>
    /// <remarks>
    /// A type change (status <c>T</c>, #158) is one raw record, but git
    /// writes its patch as two parts under the same <c>diff --git</c> line:
    /// the old side deleted, then the new side created. Between a file and
    /// a symbolic link, both sides are den files (the den reads a link to a
    /// file as that file, #112), so the change is a modification and its
    /// patch is both parts, as git wrote them; <c>git apply</c> takes them
    /// so. Between a file and a submodule (mode 160000), only the file side
    /// is a den file: the change is the file deleted or added, and its patch
    /// is that side's part alone. A type change that isn't exactly those two
    /// parts, or any other header that comes twice, is an error.
    /// <para>
    /// Git sees a link, not what it links to: its patch shows the link's
    /// target path, never the linked note's text, and a note replaced by a
    /// link is a modification even where the den doesn't read the link as a
    /// note: a link to a folder, which the den skips (#102), or a broken
    /// one, which it takes as absent (#112). A link to a file outside the
    /// den is read as that file, so for it a modification is right.
    /// </para>
    /// </remarks>
    internal static List<HistoryFileDiff> ReadDiff(string output)
    {
        var (tokens, patch) = SplitRawAndPatch(output);
        var changes = ParseChanges(tokens);
        var patches = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in SplitPatch(patch))
        {
            var header = part[..part.IndexOf('\n', StringComparison.Ordinal)];
            if (patches.TryGetValue(header, out var parts))
            {
                parts.Add(part);
            }
            else
            {
                patches.Add(header, [part]);
            }
        }

        var files = new List<HistoryFileDiff>();
        foreach (var (change, status, from, to) in changes)
        {
            var header = $"diff --git {QuotePath("a/" + from)} {QuotePath("b/" + to)}";
            if (!patches.Remove(header, out var parts))
            {
                throw new HistoryException($"Git's diff listed '{to}' but gave no patch for it.");
            }

            string part;
            if (status[0] == 'T')
            {
                if (parts.Count != 2 || !parts[0].Contains("\ndeleted file mode ", StringComparison.Ordinal)
                    || !parts[1].Contains("\nnew file mode ", StringComparison.Ordinal))
                {
                    throw new HistoryException(
                        $"Git's diff changed the type of '{to}' but didn't give its patch as the old side deleted and the new side created.");
                }

                part = change?.Kind switch
                {
                    HistoryChangeKind.Deleted => parts[0],
                    HistoryChangeKind.Added => parts[1],
                    _ => parts[0] + parts[1],
                };
            }
            else if (parts.Count == 1)
            {
                part = parts[0];
            }
            else
            {
                throw new HistoryException($"Git's diff gave {parts.Count} patches headed '{header}'.");
            }

            if (change is not null)
            {
                files.Add(new HistoryFileDiff(change, part));
            }
        }

        if (patches.Count > 0)
        {
            throw new HistoryException($"Git's diff gave a patch headed '{patches.Keys.First()}' for a file it didn't list.");
        }

        return files;
    }

    /// <summary>
    /// The raw records (as <c>-z</c> tokens) and the patch from one
    /// <c>git diff --raw --patch -z</c>: the records come first, each field
    /// ending in a NUL, then one more NUL, then the patch.
    /// </summary>
    private static (List<string> Tokens, string Patch) SplitRawAndPatch(string output)
    {
        var tokens = new List<string>();
        var at = 0;
        while (at < output.Length)
        {
            if (output[at] == '\0')
            {
                return (tokens, output[(at + 1)..]);
            }

            if (output[at] != ':')
            {
                throw new HistoryException("Git's diff didn't have the shape Wolfden asked for.");
            }

            // ':<old mode> <new mode> <old id> <new id> <status>', then one path, or two for a rename or copy.
            var record = Field();
            var status = record[(record.LastIndexOf(' ') + 1)..];
            tokens.Add(record);
            tokens.Add(Field());
            if (status.Length > 0 && status[0] is 'R' or 'C')
            {
                tokens.Add(Field());
            }
        }

        return (tokens, string.Empty);

        string Field()
        {
            var end = output.IndexOf('\0', at);
            if (end < 0)
            {
                throw new HistoryException("Git's list of changed files ended part way.");
            }

            var field = output[at..end];
            at = end + 1;
            return field;
        }
    }

    /// <summary>
    /// A path as git writes it in a <c>diff --git</c> line, with
    /// <c>core.quotepath</c> off: as it is, or in double quotes, C-style,
    /// if it holds a control character, a <c>"</c>, or a <c>\</c>.
    /// </summary>
    internal static string QuotePath(string path)
    {
        if (!path.Any(c => c < ' ' || c is '"' or '\\' or '\x7f'))
        {
            return path;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            quoted.Append(c switch
            {
                '\a' => "\\a",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when c < ' ' || c == '\x7f' => string.Create(CultureInfo.InvariantCulture, $"\\{Convert.ToString(c, 8).PadLeft(3, '0')}"),
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// Changes from git's <c>-z</c> raw output, in order. A change to a
    /// hidden path has a null change, so lists stay in step with the
    /// patches; a rename from or to a hidden path is an add or a delete.
    /// </summary>
    /// <remarks>
    /// A submodule isn't a den file (#158 review, L1). A change with a
    /// submodule on both sides, or a submodule on one and nothing on the
    /// other, has a null change too. A type change between a submodule and
    /// a file is the file added or deleted.
    /// </remarks>
    private static List<(HistoryFileChange? Change, string Status, string From, string To)> ParseChanges(IReadOnlyList<string> tokens)
    {
        var changes = new List<(HistoryFileChange?, string, string, string)>();
        for (var i = 0; i < tokens.Count;)
        {
            var token = tokens[i].Trim('\n', '\r');
            if (token.Length == 0)
            {
                i++;
                continue;
            }

            // Raw lines are ':<old mode> <new mode> <old id> <new id> <status>'.
            var fields = token.Split(' ');
            if (!token.StartsWith(':') || fields.Length != 5 || fields[4].Length == 0)
            {
                throw new HistoryException("Git's list of changed files didn't have the shape Wolfden asked for.");
            }

            var status = fields[4];
            var two = status[0] is 'R' or 'C';
            if (i + (two ? 2 : 1) >= tokens.Count)
            {
                throw new HistoryException("Git's list of changed files ended part way.");
            }

            var path = tokens[i + 1];
            var newPath = two ? tokens[i + 2] : null;
            i += two ? 3 : 2;

            var oldFile = fields[0][1..] is not (NoMode or GitlinkMode);
            var newFile = fields[1] is not (NoMode or GitlinkMode);
            var change = (oldFile, newFile, status[0]) switch
            {
                (false, false, _) => null,
                (false, true, 'T') => Change('A', path, newPath),
                (true, false, 'T') => Change('D', path, newPath),
                _ => Change(status[0], path, newPath),
            };
            changes.Add((change, status, path, newPath ?? path));
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
    /// <remarks>
    /// Git ends every line of a patch with <c>\n</c> alone, so the patch is
    /// split there and nowhere else (#161). Anything else in a note's text,
    /// a <c>\r</c>, a form feed, NEL (U+0085), or U+2028 and U+2029, stays
    /// in its line as it is: a line that holds one is still one line, so it
    /// can't start a new <c>diff --git</c> part, and a note saved with CRLF
    /// line endings shows them as git wrote them.
    /// </remarks>
    private static List<string> SplitPatch(string patch)
    {
        var parts = new List<string>();
        StringBuilder? part = null;
        foreach (var line in patch.Split('\n'))
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
    /// <paramref name="text"/> with its <c>\r\n</c> and lone <c>\r</c> line
    /// breaks made <c>\n</c>. Unlike <c>ReplaceLineEndings</c>, it leaves a
    /// form feed, NEL (U+0085), U+2028, and U+2029 as they are (#161): git
    /// doesn't take them as line breaks.
    /// </summary>
    private static string LineFeeds(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        GitCallKind kind = GitCallKind.Stoppable)
    {
        var run = Run(args, cancellationToken, input, environment, kind);
        return run.Succeeded ? OutputOf(run, GitProcess.CommandName(args)) : throw Failed(GitProcess.CommandName(args), run);
    }

    /// <summary>
    /// What git printed, or a <see cref="HistoryException"/> if it may be cut
    /// short: a process git started held its output open past the time
    /// limit. Every caller that uses git's output takes it through here.
    /// </summary>
    internal static string OutputOf(GitRun run, string command) => run.OutputComplete
        ? run.Output
        : throw new HistoryException(
            $"git {command} finished, but its output was still open when Wolfden stopped waiting, so what Wolfden read may be cut short " +
            "and wasn't used. A process git started, from a hook, a filter, or an alias, may be holding it open.");

    /// <summary>Runs git in the den's folder, or throws a <see cref="HistoryException"/> if it can't be run or doesn't finish in time.</summary>
    private GitRun Run(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        GitCallKind kind = GitCallKind.Stoppable)
    {
        try
        {
            return Start(args, cancellationToken, input, environment, kind);
        }
        catch (GitTimeoutException e)
        {
            throw TimedOut(e);
        }
    }

    /// <summary>Like <see cref="Run"/>, but a <see cref="GitTimeoutException"/> is left for the caller.</summary>
    private GitRun Start(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        GitCallKind kind = GitCallKind.Stoppable)
    {
        try
        {
            return GitProcess.Run(_options, DenRoot, args, input, environment, cancellationToken, kind);
        }
        catch (GitNotFoundException e)
        {
            throw new HistoryException($"Git couldn't be run ({e.Message}).", e);
        }
    }

    private static HistoryException Failed(string command, GitRun run) => new($"git {command} failed: {run.Said}");

    /// <summary>What to tell the writer when git didn't finish in time (#143).</summary>
    /// <param name="e">The timeout.</param>
    /// <param name="retry">For a git left running, what to do before trying again, if a plain retry could clash with what git does when it finishes.</param>
    private static HistoryException TimedOut(GitTimeoutException e, string? retry = null) => e.StillRunning is null
        ? new HistoryException(
            $"{e.Message}, so Wolfden stopped it, and nothing was changed. Git may be waiting on a credential helper or a filter " +
            "(such as Git LFS), or the repository may be very large.",
            e)
        : new HistoryException(
            $"{e.Message}. Wolfden stopped waiting but left git running, since stopping it part way would leave the repository locked. " +
            $"{LikelyCauses} Answer or close the prompt, or end that git process. {retry ?? "Then try again."}",
            e);

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
