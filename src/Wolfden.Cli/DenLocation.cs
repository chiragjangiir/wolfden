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

    /// <summary>
    /// For tests: when set, a search that starts inside this folder stops
    /// at it and never looks above it, so a test's result doesn't depend on
    /// what's above the temporary folder on the machine running it. A
    /// search that starts elsewhere isn't affected.
    /// </summary>
    internal static string? Ceiling { get; set; }

    /// <summary>The nearest folder at or above <paramref name="start"/> that holds a <c>den.yaml</c>, or null.</summary>
    public static string? FindAbove(string start)
    {
        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        var ceiling = Ceiling is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Ceiling));
        if (ceiling is not null && !IsAtOrInside(from, ceiling))
        {
            ceiling = null;
        }

        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, DenFile)))
            {
                return dir.FullName;
            }

            if (ceiling is not null && string.Equals(Path.TrimEndingDirectorySeparator(dir.FullName), ceiling, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;

        static bool IsAtOrInside(string path, string folder) =>
            string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many folder levels below a new den's folder <see cref="FindBelow"/> looks.</summary>
    internal const int BelowDepth = 4;

    /// <summary>The most folders <see cref="FindBelow"/> looks in, so it stays quick in a large folder such as a home folder.</summary>
    internal const int BelowFolders = 2000;

    /// <summary>
    /// A folder below <paramref name="root"/> that holds a <c>den.yaml</c>, or
    /// null. It's a bounded search, for <c>init</c>: it goes at most
    /// <see cref="BelowDepth"/> folder levels down and looks in at most
    /// <see cref="BelowFolders"/> folders, nearest levels first. It skips
    /// folders whose names start with <c>.</c> (F-LAY-2) and folder links
    /// (symbolic links and junctions), as the den walk does.
    /// </summary>
    public static string? FindBelow(string root)
    {
        var level = new List<string> { Path.GetFullPath(root) };
        var visited = 0;
        for (var depth = 1; depth <= BelowDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var folder in level)
            {
                IEnumerable<DirectoryInfo> children;
                try
                {
                    children = new DirectoryInfo(folder).EnumerateDirectories();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                try
                {
                    foreach (var child in children)
                    {
                        if (child.Name.StartsWith('.') || Core.Structure.FileSystemDenSource.IsFolderLink(child))
                        {
                            continue;
                        }

                        if (++visited > BelowFolders)
                        {
                            return null;
                        }

                        if (File.Exists(Path.Combine(child.FullName, DenFile)))
                        {
                            return child.FullName;
                        }

                        next.Add(child.FullName);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // A folder that can't be listed is skipped, as if it were empty.
                }
            }

            level = next;
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
