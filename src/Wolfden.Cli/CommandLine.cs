namespace Wolfden.Cli;

/// <summary>
/// What a command accepts: its usage line and help, its flags, the options
/// that take a value, and how many plain arguments it takes.
/// </summary>
/// <param name="Name">The command, such as <c>check</c>.</param>
/// <param name="Usage">The usage line, starting <c>usage: wolfden</c>.</param>
/// <param name="Help">What the command does and what each option means, shown by <c>--help</c> after the usage line.</param>
/// <param name="Flags">Options without a value, such as <c>--json</c>.</param>
/// <param name="ValueOptions">Options that take one value, such as <c>--den &lt;folder&gt;</c>.</param>
/// <param name="RepeatableOptions">Value options that may be given more than once, such as <c>--type</c>.</param>
/// <param name="MaxArguments">The most plain arguments the command takes.</param>
/// <param name="TooManyArguments">The message when there are more.</param>
internal sealed record CommandSpec(
    string Name,
    string Usage,
    string Help,
    string[] Flags,
    string[] ValueOptions,
    string[] RepeatableOptions,
    int MaxArguments,
    string TooManyArguments);

/// <summary>
/// A parsed command line. The parser is deliberately small: <c>--name</c>
/// flags, <c>--name value</c> or <c>--name=value</c> options, plain
/// arguments, and <c>--</c> to end the options, so a plain argument can
/// start with <c>-</c>.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private CommandLine()
    {
    }

    /// <summary>The plain arguments, in order.</summary>
    public List<string> Arguments { get; } = [];

    /// <summary>True if the flag was given.</summary>
    public bool Has(string flag) => _flags.Contains(flag);

    /// <summary>The option's value, or null if it wasn't given.</summary>
    public string? Value(string option) => _values.TryGetValue(option, out var values) ? values[^1] : null;

    /// <summary>Every value given for a repeatable option, in order.</summary>
    public IReadOnlyList<string> Values(string option) => _values.TryGetValue(option, out var values) ? values : [];

    /// <summary>
    /// Parses <paramref name="args"/> for <paramref name="spec"/>. Returns
    /// null to go on with <paramref name="line"/>, or the exit code to stop
    /// with: 0 after printing help, or <see cref="CliApp.ExitUsage"/> after
    /// reporting a mistake.
    /// </summary>
    public static int? Parse(CommandSpec spec, IReadOnlyList<string> args, TextWriter output, TextWriter error, out CommandLine line)
    {
        line = new CommandLine();
        var optionsEnded = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (optionsEnded || arg is "-" || !arg.StartsWith('-'))
            {
                if (line.Arguments.Count >= spec.MaxArguments)
                {
                    return Fail(spec.TooManyArguments);
                }

                line.Arguments.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (arg is "--help" or "-h")
            {
                output.WriteLine(spec.Usage);
                output.WriteLine();
                output.Write(spec.Help.ReplaceLineEndings("\n").TrimEnd('\n').ReplaceLineEndings(output.NewLine));
                output.WriteLine();
                return CliApp.ExitOk;
            }

            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? arg[..equals] : arg;
            if (spec.Flags.Contains(name))
            {
                if (equals > 0)
                {
                    return Fail($"'{name}' doesn't take a value.");
                }

                line._flags.Add(name);
            }
            else if (spec.ValueOptions.Contains(name))
            {
                string value;
                if (equals > 0)
                {
                    value = arg[(equals + 1)..];
                }
                else if (i + 1 < args.Count)
                {
                    value = args[++i];
                }
                else
                {
                    return Fail($"'{name}' needs a value.");
                }

                if (line._values.TryGetValue(name, out var values))
                {
                    if (!spec.RepeatableOptions.Contains(name))
                    {
                        return Fail($"give '{name}' only once.");
                    }

                    values.Add(value);
                }
                else
                {
                    line._values[name] = [value];
                }
            }
            else
            {
                return Fail($"unknown option '{name}'.");
            }
        }

        return null;

        int Fail(string message)
        {
            error.WriteLine($"wolfden {spec.Name}: {message}");
            error.WriteLine(spec.Usage);
            return CliApp.ExitUsage;
        }
    }
}
