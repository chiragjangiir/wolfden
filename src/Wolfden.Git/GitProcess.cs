using System.ComponentModel;
using System.Diagnostics;
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

/// <summary>
/// Runs git commands. Arguments are passed one by one, never through a
/// shell. Git never waits on the terminal: it can't prompt for a password
/// (<c>GIT_TERMINAL_PROMPT=0</c>), open an editor, or start a pager, and its
/// standard input is closed once any input is written.
/// </summary>
internal static class GitProcess
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Runs <c>git &lt;args&gt;</c> in <paramref name="folder"/> and waits for it.
    /// </summary>
    /// <param name="options">Which git, and its environment.</param>
    /// <param name="folder">The folder to run it in.</param>
    /// <param name="args">The arguments, each passed as is.</param>
    /// <param name="input">Text for its standard input, or null for none.</param>
    /// <param name="environment">More environment variables for this command only, such as <c>GIT_INDEX_FILE</c>.</param>
    /// <param name="cancellationToken">Stops git, and throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="GitNotFoundException">Git couldn't be started.</exception>
    public static GitRun Run(
        GitOptions options,
        string folder,
        IEnumerable<string> args,
        string? input = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        foreach (var arg in new[] { "-c", "core.quotepath=false", "-c", "color.ui=false" }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_EDITOR"] = "false";
        start.Environment["GIT_SEQUENCE_EDITOR"] = "false";
        Apply(start, options.Environment);
        if (environment is not null)
        {
            Apply(start, environment);
        }

        using var process = new Process { StartInfo = start };
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
        using var kill = cancellationToken.Register(() =>
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
        });

        try
        {
            if (input is not null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Git exited without reading its input; its exit code says why.
        }

        process.WaitForExit();

        // Once git is stopped, don't wait for its output: a child it started,
        // such as a hook's 'sleep', can hold the pipes open after it's gone.
        cancellationToken.ThrowIfCancellationRequested();
        Task.WaitAll(output, error);
        return new GitRun(process.ExitCode, output.Result, error.Result);
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
