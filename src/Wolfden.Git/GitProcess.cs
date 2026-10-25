using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wolfden.Git;

/// <summary>What one git command did.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">Its standard output, as UTF-8.</param>
/// <param name="Error">Its standard error, as UTF-8.</param>
internal sealed record GitRun(int ExitCode, string Output, string Error)
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
internal static class GitProcess
{
    private static readonly UTF8Encoding Utf8 = new(false);

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

            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

            // Written on the side, so a git that doesn't read its input yet
            // (a commit reads its message after the pre-commit hook) can't
            // hold this call up past its time limit.
            var writing = Task.Run(
                () =>
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
                },
                CancellationToken.None);

            using var kill = stoppable ? cancellationToken.Register(() => Stop(process)) : default;
            if (!process.WaitForExit(timeout))
            {
                if (stoppable)
                {
                    Stop(process);
                    Abandon(output, error, writing);
                    throw new GitTimeoutException(CommandName(argList), timeout, null);
                }

                // Never stopped part way: it finishes on its own, and the
                // task says what it did then. The task owns the process now.
                handedOff = true;
                var stillRunning = Task.Run(
                    () =>
                    {
                        using (process)
                        {
                            process.WaitForExit();
                            Task.WaitAll(output, error, writing);
                            return new GitRun(process.ExitCode, output.Result, error.Result);
                        }
                    },
                    CancellationToken.None);
                Abandon(stillRunning);
                throw new GitTimeoutException(CommandName(argList), timeout, stillRunning);
            }

            if (stoppable && cancellationToken.IsCancellationRequested)
            {
                // Once git is stopped, don't wait for its output: a child it
                // started, such as a hook's 'sleep', can hold the pipes open
                // after it's gone.
                Abandon(output, error, writing);
                cancellationToken.ThrowIfCancellationRequested();
            }

            Task.WaitAll(output, error, writing);
            return new GitRun(process.ExitCode, output.Result, error.Result);
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
        if (time == System.Threading.Timeout.InfiniteTimeSpan)
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

    /// <summary>
    /// Leaves <paramref name="tasks"/> running with nothing waiting on them,
    /// and observes any fault they end with, so it isn't left unobserved:
    /// once git is stopped or left behind, its pipes may fail.
    /// </summary>
    private static void Abandon(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

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
}
