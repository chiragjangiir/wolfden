using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wolfden.Git;

/// <summary>What one git command did.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">Its standard output, as UTF-8.</param>
/// <param name="Error">Its standard error, as UTF-8.</param>
/// <param name="OutputComplete">
/// True if <paramref name="Output"/> was read to its end. False if it was
/// still open when Wolfden stopped waiting, because a process git started
/// (from a hook, a filter, or an alias) held it open after git exited: then
/// <paramref name="Output"/> may be cut short, and a caller that uses it
/// must not trust it.
/// </param>
internal sealed record GitRun(int ExitCode, string Output, string Error, bool OutputComplete = true)
{
    /// <summary>True if it exited 0.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>What git said about a failure, on one line, for a message.</summary>
    public string Said
    {
        get
        {
            var text = string.Join(" ", (Error.Length > 0 ? Error : Output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return text.Length == 0 ? $"git exited with code {ExitCode}" : text;
        }
    }
}

/// <summary>Git can't be run at all: it isn't installed, or isn't on the PATH.</summary>
internal sealed class GitNotFoundException(string message, Exception inner) : Exception(message, inner);

/// <summary>Whether a git command may be stopped part way.</summary>
internal enum GitCallKind
{
    /// <summary>
    /// It changes nothing the user's repository needs intact: it reads, or
    /// writes only a temporary index of Wolfden's own. It runs with
    /// <c>GIT_OPTIONAL_LOCKS=0</c>, so it doesn't refresh the user's index on
    /// the side, and it's stopped (its whole process tree) when it's
    /// cancelled or takes longer than <see cref="GitOptions.ReadTimeout"/>.
    /// Its output is waited for within the same limit.
    /// </summary>
    Stoppable,

    /// <summary>
    /// It writes the user's index, refs, or repository (<c>add</c>,
    /// <c>commit</c>, <c>rm --cached</c>, <c>tag</c>, <c>init</c>), taking
    /// git's lock files to do it. Once started it's never stopped (#145):
    /// killed part way, git would leave <c>.git/index.lock</c> behind, and
    /// every git command in the repository would fail until someone deleted
    /// it by hand. The cancellation token is checked only before git starts.
    /// After <see cref="GitOptions.WriteTimeout"/>, Wolfden stops waiting but
    /// leaves git running to finish on its own (<see cref="GitTimeoutException.StillRunning"/>).
    /// Its exit code is what counts: once git exits, its output is waited
    /// for only briefly, since a hook's background child (a post-commit
    /// <c>git push &amp;</c>) can hold it open long after.
    /// </summary>
    MustFinish,
}

/// <summary>A git command didn't finish within its time limit.</summary>
internal sealed class GitTimeoutException : Exception
{
    public GitTimeoutException(string command, TimeSpan timeout, Task<GitRun>? stillRunning)
        : base($"git {command} didn't finish within {GitProcess.Describe(timeout)}")
    {
        Command = command;
        Timeout = timeout;
        StillRunning = stillRunning;
    }

    /// <summary>The git command, such as <c>commit</c>.</summary>
    public string Command { get; }

    /// <summary>How long Wolfden waited.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// For a <see cref="GitCallKind.MustFinish"/> command, git still running:
    /// a task that completes with what it did once it exits (or faults if
    /// that can't be read). Null for a <see cref="GitCallKind.Stoppable"/>
    /// one, which was stopped.
    /// </summary>
    public Task<GitRun>? StillRunning { get; }
}

/// <summary>
/// Runs git commands. Arguments are passed one by one, never through a
/// shell. Git never waits on the terminal: it can't prompt for a password
/// (<c>GIT_TERMINAL_PROMPT=0</c>), open an editor, or start a pager, and its
/// standard input is closed once any input is written. It can still wait on
/// something outside the terminal, such as a passphrase window for commit
/// signing, a stuck hook, or a credential helper, so every command has a
/// time limit (#143). <see cref="GitCallKind"/> says what happens then.
/// </summary>
/// <remarks>
/// Git's input and output are written and read on threads of their own,
/// never the thread pool's: git can exit before anyone has read output that
/// fits in the pipe's buffer, and with a busy pool, a reader waiting for a
/// pool thread would take so long that output would look empty or cut
/// short.
/// </remarks>
internal static class GitProcess
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// How long to wait for a <see cref="GitCallKind.MustFinish"/> command's
    /// output, and any command's standard error, after git exits. The pipes
    /// are read all along on threads of their own, so by then there's at
    /// most a buffer's worth left to drain.
    /// </summary>
    private static readonly TimeSpan PipeGrace = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for git to go after it's been killed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <c>git &lt;args&gt;</c> in <paramref name="folder"/> and waits for it.
    /// </summary>
    /// <param name="options">Which git, its environment, and its time limits.</param>
    /// <param name="folder">The folder to run it in.</param>
    /// <param name="args">The arguments, each passed as is.</param>
    /// <param name="input">Text for its standard input, or null for none.</param>
    /// <param name="environment">More environment variables for this command only, such as <c>GIT_INDEX_FILE</c>.</param>
    /// <param name="cancellationToken">
    /// Stops a <see cref="GitCallKind.Stoppable"/> command, and throws
    /// <see cref="OperationCanceledException"/>. A <see cref="GitCallKind.MustFinish"/>
    /// command checks it only before git starts.
    /// </param>
    /// <param name="kind">Whether git may be stopped part way.</param>
    /// <exception cref="GitNotFoundException">Git couldn't be started.</exception>
    /// <exception cref="GitTimeoutException">Git didn't finish in time.</exception>
    public static GitRun Run(
        GitOptions options,
        string folder,
        IEnumerable<string> args,
        string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default,
        GitCallKind kind = GitCallKind.Stoppable)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stoppable = kind == GitCallKind.Stoppable;
        var timeout = stoppable ? options.ReadTimeout : options.WriteTimeout;
        var argList = args.ToList();
        var start = new ProcessStartInfo(options.Executable)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        // Settings that only keep git from waiting on a person or a terminal.
        // The user's own configuration (signing, hooks, safe.directory) still applies.
        foreach (var arg in new[] { "-c", "core.quotepath=false", "-c", "color.ui=false" }.Concat(argList))
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_EDITOR"] = "false";
        start.Environment["GIT_SEQUENCE_EDITOR"] = "false";
        if (stoppable)
        {
            // Otherwise a read may refresh the user's index on the side,
            // taking index.lock, and stopping it then would leave the lock.
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        Apply(start, options.Environment);
        if (environment is not null)
        {
            Apply(start, environment);
        }

        var process = new Process { StartInfo = start };
        var handedOff = false;
        try
        {
            try
            {
                process.Start();
            }
            catch (Win32Exception e)
            {
                throw new GitNotFoundException($"couldn't run '{options.Executable}': {e.Message}", e);
            }

            var clock = Stopwatch.StartNew();
            var output = new Capture(process.StandardOutput, "git stdout");
            var error = new Capture(process.StandardError, "git stderr");

            // Written on the side, so a git that doesn't read its input yet
            // (a commit reads its message after the pre-commit hook) can't
            // hold this call up past its time limit.
            StartThread("git stdin", () =>
            {
                try
                {
                    if (input is not null)
                    {
                        process.StandardInput.Write(input);
                    }

                    process.StandardInput.Close();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Git exited, or was stopped, without reading its input; its exit code says why.
                }
            });

            using var kill = stoppable ? cancellationToken.Register(() => Stop(process)) : default;
            if (!process.WaitForExit(timeout))
            {
                if (stoppable)
                {
                    Stop(process);

                    // Kill returns before git has gone (on Windows, at
                    // least); wait a little, so the caller can clean up a
                    // temporary index git was writing.
                    process.WaitForExit(KillWait);
                    throw new GitTimeoutException(CommandName(argList), timeout, null);
                }

                // Never stopped part way: it finishes on its own, and the
                // task says what it did then. The task owns the process now.
                handedOff = true;
                var stillRunning = FinishAsync(process, output, error);
                _ = stillRunning.ContinueWith(
                    t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new GitTimeoutException(CommandName(argList), timeout, stillRunning);
            }

            if (stoppable && cancellationToken.IsCancellationRequested)
            {
                // Cancelled: git was stopped, or it had just finished, in
                // which case what it did is thrown away. A Stoppable command
                // changed nothing of the user's, so that loses nothing.
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (stoppable)
            {
                // Its output is what it's for: wait for all of it, within
                // what's left of the time limit (and at least the grace).
                var left = timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : timeout - clock.Elapsed;
                output.Wait(left == Timeout.InfiniteTimeSpan || left > PipeGrace ? left : PipeGrace, cancellationToken);
            }
            else
            {
                output.Wait(PipeGrace, CancellationToken.None);
            }

            error.Wait(PipeGrace, stoppable ? cancellationToken : CancellationToken.None);
            return Result(process, output, error);
        }
        finally
        {
            if (!handedOff)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>How long <paramref name="time"/> is, in words: "2 minutes", "30 seconds", "0.5 seconds".</summary>
    internal static string Describe(TimeSpan time)
    {
        if (time == Timeout.InfiniteTimeSpan)
        {
            return "any time";
        }

        if (time.TotalMinutes >= 1 && time.TotalSeconds % 60 == 0)
        {
            var minutes = (long)time.TotalMinutes;
            return minutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
        }

        var seconds = time.TotalSeconds;
        return seconds == 1 ? "1 second" : string.Create(CultureInfo.InvariantCulture, $"{seconds:0.###} seconds");
    }

    /// <summary>The git command in <paramref name="args"/>: the first argument that isn't an option, or the value of <c>-c</c>.</summary>
    internal static string CommandName(IReadOnlyList<string> args)
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

    /// <summary>What git did, from its exit code and what was read of its output.</summary>
    private static GitRun Result(Process process, Capture output, Capture error)
    {
        // Ended first: if it's true, the text is all there.
        var complete = output.Ended;
        return new GitRun(process.ExitCode, output.Text, error.Text, complete);
    }

    /// <summary>Waits, without holding a thread, for a git left running to exit, then says what it did.</summary>
    private static async Task<GitRun> FinishAsync(Process process, Capture output, Capture error)
    {
        using (process)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(output.Completion, error.Completion), Task.Delay(PipeGrace)).ConfigureAwait(false);
            return Result(process, output, error);
        }
    }

    /// <summary>Kills git and everything it started, if it's still running.</summary>
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It has already exited.
        }
        catch (Win32Exception)
        {
        }
    }

    private static void StartThread(string name, Action work) =>
        new Thread(() => work()) { IsBackground = true, Name = name }.Start();

    private static void Apply(ProcessStartInfo start, IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
    }

    /// <summary>
    /// Reads a stream to its end on a thread of its own, with blocking reads,
    /// keeping what it has read so far where <see cref="Text"/> can take it at
    /// any time, so output can be used, or known to be cut short, even when
    /// the stream never ends. Its <see cref="Completion"/> never faults.
    /// </summary>
    private sealed class Capture
    {
        private readonly StringBuilder _text = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _ended;

        public Capture(StreamReader reader, string name) => StartThread(name, () => Pump(reader));

        /// <summary>Completes when reading stops: at the end of the stream, or when a read fails (as when git's process is disposed first).</summary>
        public Task Completion => _done.Task;

        /// <summary>True once the stream has been read to its end.</summary>
        public bool Ended
        {
            get
            {
                lock (_text)
                {
                    return _ended;
                }
            }
        }

        /// <summary>What has been read so far.</summary>
        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        /// <summary>Waits up to <paramref name="time"/> for reading to stop.</summary>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled first.</exception>
        public void Wait(TimeSpan time, CancellationToken cancellationToken) =>
            Completion.Wait((int)Math.Min(int.MaxValue, time == Timeout.InfiniteTimeSpan ? -1 : Math.Max(0, time.TotalMilliseconds)), cancellationToken);

        private void Pump(StreamReader reader)
        {
            try
            {
                var buffer = new char[8192];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (_text)
                    {
                        _text.Append(buffer, 0, read);
                    }
                }

                lock (_text)
                {
                    _ended = true;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Reading stopped part way; Ended stays false.
            }
            finally
            {
                _done.TrySetResult();
            }
        }
    }
}
