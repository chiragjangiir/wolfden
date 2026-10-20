namespace Wolfden.Cli;

/// <summary>
/// Which den a command works on. A den named on the command line is used
/// as given. Otherwise it's the current folder, or the nearest folder above
/// it, that holds a <c>den.yaml</c>, so a command run anywhere inside a den
/// finds it, as git finds its repository.
/// </summary>
internal static class DenLocation
{
    internal const string DenFile = "den.yaml";

    /// <summary>The nearest folder at or above <paramref name="start"/> that holds a <c>den.yaml</c>, or null.</summary>
    public static string? FindAbove(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, DenFile)))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// The den for a command that needs one to exist (<c>index</c>,
    /// <c>query</c>): <paramref name="given"/> if it's a folder with a
    /// <c>den.yaml</c>, or the den found from the current folder. Returns
    /// null after reporting the problem.
    /// </summary>
    public static string? Resolve(string command, string? given, TextWriter error)
    {
        if (given is not null)
        {
            if (!Directory.Exists(given))
            {
                error.WriteLine($"wolfden {command}: there's no folder at '{given}'.");
                return null;
            }

            if (!File.Exists(Path.Combine(given, DenFile)))
            {
                error.WriteLine($"wolfden {command}: '{given}' has no den.yaml, so it isn't a den. Create one there with 'wolfden init'.");
                return null;
            }

            return given;
        }

        var found = FindAbove(Environment.CurrentDirectory);
        if (found is null)
        {
            error.WriteLine($"wolfden {command}: there's no den here: neither this folder nor any folder above it has a den.yaml. Name one with '--den <folder>', or create one with 'wolfden init'.");
        }

        return found;
    }

    /// <summary>
    /// The den folder from a command's optional plain argument and its
    /// <c>--den</c> option, which name the same thing. Returns false after
    /// reporting that both were given.
    /// </summary>
    public static bool TryGetGiven(CommandSpec spec, CommandLine line, TextWriter error, out string? given)
    {
        var argument = line.Arguments.Count > 0 ? line.Arguments[0] : null;
        var option = line.Value("--den");
        if (argument is not null && option is not null)
        {
            error.WriteLine($"wolfden {spec.Name}: give the den folder once, as an argument or with '--den', not both.");
            error.WriteLine(spec.Usage);
            given = null;
            return false;
        }

        given = argument ?? option;
        return true;
    }
}
