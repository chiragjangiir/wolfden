using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;
using Wolfden.Core.Yaml;

namespace Wolfden.Cli;

/// <summary>
/// <c>wolfden init [&lt;folder&gt;] [--name &lt;name&gt;] [--universe &lt;key&gt;] [--json]</c>:
/// makes a new den (format.md §5): a <c>den.yaml</c>, and with
/// <c>--universe</c> a first universe, so <c>wolfden check</c> passes on
/// it with no errors or warnings.
/// </summary>
/// <remarks>
/// It creates the folder if needed and never overwrites anything: a folder
/// that already has a <c>den.yaml</c>, or that is inside a den (dens don't
/// nest, F-LAY-1), is refused with exit code 2. Other files already in the
/// folder, such as a README or a <c>.git</c> folder, are left alone.
/// </remarks>
internal static class InitCommand
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static readonly CommandSpec Spec = new(
        "init",
        "usage: wolfden init [<folder>] [--name <name>] [--universe <key>] [--json]",
        """
        Creates a new den: a den.yaml, and with --universe a first universe.

          <folder>          where to create it (made if it doesn't exist); by
                            default, the current folder
          --name <name>     the den's display name; by default, the folder's name
          --universe <key>  also create universes/<key>/universe.md; the key is
                            lowercase letters, digits, and hyphens, starting
                            with a letter
          --json            print one JSON document instead of text

        It never overwrites anything, and won't create a den inside another den.
        Exits 0 when the den is created and 2 if it couldn't be.
        """,
        ["--json"],
        ["--name", "--universe"],
        [],
        1,
        "give at most one folder.");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (CommandLine.Parse(Spec, args, output, error, out var line) is { } exit)
        {
            return exit;
        }

        var folder = line.Arguments.Count > 0 ? line.Arguments[0] : ".";
        var name = line.Value("--name");
        var universe = line.Value("--universe");
        if (name is not null && name.Trim().Length == 0)
        {
            return Fail("the name can't be empty.");
        }

        if (universe is not null && !Keys.IsContainerKey(universe))
        {
            return Fail($"'{universe}' isn't a valid universe key: use lowercase letters, digits, and single hyphens, starting with a letter, such as 'the-long-dark' (F-KEY-1, F-KEY-2).");
        }

        if (File.Exists(folder))
        {
            return Fail($"'{folder}' is a file, not a folder.");
        }

        var root = Path.GetFullPath(folder);
        if (File.Exists(Path.Combine(root, DenLocation.DenFile)))
        {
            return Fail($"'{folder}' is already a den: it has a den.yaml. Nothing was changed.");
        }

        var parent = Directory.GetParent(root);
        if (parent is not null && DenLocation.FindAbove(parent.FullName) is { } outer)
        {
            return Fail($"'{folder}' is inside the den at '{outer}', and dens don't nest (F-LAY-1). Choose a folder outside it.");
        }

        var created = new List<string>();
        // Everything this run creates, folders included, so a failure part way can undo it.
        var made = new List<string>();
        try
        {
            CreateFolder(root, made);
            WriteNew(root, DenLocation.DenFile, DenYaml(name));
            made.Add(Path.Combine(root, DenLocation.DenFile));
            created.Add(DenLocation.DenFile);
            if (universe is not null)
            {
                var path = $"universes/{universe}/universe.md";
                CreateFolder(Path.Combine(root, "universes"), made);
                CreateFolder(Path.Combine(root, "universes", universe), made);
                WriteNew(root, path, UniverseFile(universe));
                created.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var left = Undo(made);
            var leftNote = left.Count == 0
                ? " Nothing was left behind."
                : $" These couldn't be removed and are still there: {string.Join(", ", left.Select(p => $"'{p}'"))}.";
            return Fail($"couldn't create the den at '{folder}': {e.Message}{leftNote}");
        }

        if (line.Has("--json"))
        {
            CliJson.Write(output, new InitReport(1, root, created));
        }
        else
        {
            foreach (var file in created)
            {
                output.WriteLine($"created {file}");
            }

            output.WriteLine();
            output.WriteLine($"Created a den at '{root}'.");
            output.WriteLine(universe is null
                ? "Next: add a universe (format.md §6.2), then run 'wolfden check'. Wolfden doesn't run git for you; 'git init' makes the den a repository."
                : "Next: add series, books, and entities (format.md §5.1), then run 'wolfden check'. Wolfden doesn't run git for you; 'git init' makes the den a repository.");
        }

        return CliApp.ExitOk;

        int Fail(string message)
        {
            error.WriteLine($"wolfden init: {message}");
            return CliApp.ExitUsage;
        }
    }

    /// <summary>
    /// <c>den.yaml</c>: the format, the name if one was given, and the main
    /// optional settings as comments, so a writer can find them.
    /// </summary>
    internal static byte[] DenYaml(string? name)
    {
        var start = "# Wolfden den settings. The format is described in Wolfden's docs/format.md, section 5.2.\nformat: 1\n";
        // The editor quotes the name if it has to, so it stays a string.
        var text = name is null
            ? start + "# name: My Notes           # display name; defaults to the folder's name\n"
            : YamlFile.Parse(Utf8.GetBytes(start)).WithValue("name", YamlScalarValue.String(name)).Text;
        text += "# spoiler_checks: true     # flag links to things the reader hasn't met yet (F-DEN-7)\n";
        return Utf8.GetBytes(text);
    }

    /// <summary>A universe file with a new id, a name made from its key, and a line of overview to replace.</summary>
    internal static byte[] UniverseFile(string key)
    {
        var name = string.Join(' ', key.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return MarkdownFile.Parse(Utf8.GetBytes("---\n---\n\nWhat this universe is: its setting, and the works in it.\n"), requireFrontmatter: true)
            .WithId(Ids.New())
            .WithFrontmatterValue("name", YamlScalarValue.String(name))
            .ToBytes();
    }

    /// <summary>Creates a folder and any missing folders above it, recording in <paramref name="made"/> each one this run made, outermost first.</summary>
    private static void CreateFolder(string path, List<string> made)
    {
        var missing = new Stack<string>();
        for (var dir = Path.GetFullPath(path); !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!)
        {
            missing.Push(dir);
        }

        foreach (var dir in missing)
        {
            Directory.CreateDirectory(dir);
            made.Add(dir);
        }
    }

    /// <summary>Removes what this run made, newest first, and returns what couldn't be removed.</summary>
    private static List<string> Undo(List<string> made)
    {
        var left = new List<string>();
        for (var i = made.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(made[i]))
                {
                    File.Delete(made[i]);
                }
                else if (Directory.Exists(made[i]))
                {
                    Directory.Delete(made[i]);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add(made[i]);
            }
        }

        return left;
    }

    /// <summary>Writes a new file, failing if one is already there.</summary>
    private static void WriteNew(string root, string path, byte[] bytes)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    /// <summary>The JSON shape of <c>wolfden init --json</c>.</summary>
    /// <param name="Version">The shape's version, 1.</param>
    /// <param name="Den">The den root's full path.</param>
    /// <param name="Created">The files created, relative to the den root, with <c>/</c>.</param>
    internal sealed record InitReport(int Version, string Den, IReadOnlyList<string> Created);
}
